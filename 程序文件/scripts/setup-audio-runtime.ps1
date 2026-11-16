$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
function Initialize-PythonBootstrap([string]$runtimeDirectory, [string]$temporaryDirectory) {
    # A canceled extraction can leave python.exe without its DLL or standard
    # library. Recreate only the embedded core, preserving installed packages.
    $requiredCore = @('python.exe', 'python310.dll', 'python3.dll', 'python310.zip')
    $missingCore = @($requiredCore | Where-Object { -not (Test-Path -LiteralPath (Join-Path $runtimeDirectory $_) -PathType Leaf) })
    if ($missingCore.Count -eq 0) { return }
    Write-Output '正在准备独立 Python 音频运行环境（保留已下载的识别模型）...'
    $archivePath = Join-Path $temporaryDirectory 'python.zip'
    $stagedCore = Join-Path $temporaryDirectory 'python-core'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://www.python.org/ftp/python/3.10.11/python-3.10.11-embed-amd64.zip' -OutFile $archivePath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $stagedCore)
    foreach ($required in $requiredCore) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagedCore $required) -PathType Leaf)) { throw 'Python 安装包缺少核心文件，请重新下载。' }
    }
    # Validate the downloaded archive completely before overwriting any core file.
    # The official embeddable distribution contains only files at its top level.
    $files = @(Get-ChildItem -LiteralPath $stagedCore -Force)
    if (@($files | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count -ne 0) {
        throw 'Python 安装包结构无效，请重新下载。'
    }
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $runtimeDirectory $file.Name) -Force }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $projectRoot 'runtime\python'
$pythonPath = Join-Path $runtimeDirectory 'python.exe'
$packageDirectory = Join-Path $runtimeDirectory 'Lib\site-packages'
$temporaryDirectory = if ($env:HARMONICA_SETUP_TEMP) { [IO.Path]::GetFullPath($env:HARMONICA_SETUP_TEMP) } else { Join-Path $projectRoot ('.audio-setup-' + [Guid]::NewGuid().ToString('N')) }
if (-not $temporaryDirectory.StartsWith(([IO.Path]::GetFullPath($projectRoot) + '\'), [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $temporaryDirectory).StartsWith('.audio-setup-')) { throw '安装暂存路径无效。' }
New-Item -ItemType Directory -Path $runtimeDirectory,$packageDirectory,$temporaryDirectory -Force | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try {
    Initialize-PythonBootstrap $runtimeDirectory $temporaryDirectory
    [IO.File]::WriteAllText((Join-Path $runtimeDirectory 'python310._pth'), "python310.zip`n.`nLib\site-packages`nimport site`n", [Text.Encoding]::ASCII)
    $modelCheck = "from basic_pitch.inference import Model; from basic_pitch import ICASSP_2022_MODEL_PATH; Model(ICASSP_2022_MODEL_PATH); print('AUDIO_RUNTIME_READY')"
    $reuseInstalledModel = $false
    if (Test-Path -LiteralPath (Join-Path $packageDirectory 'basic_pitch\saved_models\icassp_2022\nmp.onnx')) {
        Write-Output '正在检查已有识别组件，可用时直接继续，不重复下载...'
        $probePreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $pythonPath -I -B -c $modelCheck 2>$null
            $reuseInstalledModel = $LASTEXITCODE -eq 0
        }
        finally { $ErrorActionPreference = $probePreference }
    }
    if (-not $reuseInstalledModel) {
        $wheelPath = Join-Path $temporaryDirectory 'pip.whl'
        $release = Invoke-RestMethod -Uri 'https://pypi.org/pypi/pip/25.0.1/json'
        $wheel = $release.urls | Where-Object { $_.filename -eq 'pip-25.0.1-py3-none-any.whl' } | Select-Object -First 1
        if (-not $wheel -or -not $wheel.url.StartsWith('https://files.pythonhosted.org/')) { throw '无法验证 pip 下载地址。' }
        Invoke-WebRequest -UseBasicParsing -Uri $wheel.url -OutFile $wheelPath
        if ((Get-FileHash -LiteralPath $wheelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wheel.digests.sha256) { throw 'pip 下载校验失败。' }
        $bootstrap = 'import sys; sys.path.insert(0, sys.argv.pop(1)); from pip._internal.cli.main import main; sys.exit(main())'
        & $pythonPath -I -B -c $bootstrap $wheelPath install --disable-pip-version-check --no-cache-dir --upgrade --target $packageDirectory 'pip==25.0.1' 'setuptools==75.8.2' 'wheel==0.45.1'
        if ($LASTEXITCODE -ne 0) { throw 'Python 安装工具初始化失败。' }
        Write-Output '正在安装 Basic Pitch / ONNX 音频转 MIDI 模型...'
        & $pythonPath -I -B -m pip install --disable-pip-version-check --no-cache-dir --no-build-isolation --upgrade --target $packageDirectory -r (Join-Path $PSScriptRoot 'audio-requirements.txt')
        if ($LASTEXITCODE -ne 0) { throw '音频转 MIDI 组件安装失败，请检查网络后重试。' }
        & $pythonPath -I -B -c $modelCheck
        if ($LASTEXITCODE -ne 0) { throw '音频模型初始化失败。' }
        }
    Write-Output '正在优化识别组件占用，保留原始识别模型...'
    & $pythonPath -I -B (Join-Path $PSScriptRoot 'compact-audio-runtime.py') $runtimeDirectory
    if ($LASTEXITCODE -ne 0) { throw '音频组件整理失败，请重试。' }
    & $pythonPath -I -B -c $modelCheck
    if ($LASTEXITCODE -ne 0) { throw '精简后的音频模型验证失败。' }
    # The version records an optimization attempt. Unsupported compression is
    # optional and must not force another installation on every conversion.
    [IO.File]::WriteAllText((Join-Path $runtimeDirectory 'ready.txt'), 'Basic Pitch 0.4.0 / ONNX Runtime 1.19.2 / Python 3.10.11 / Storage optimization v1', [Text.Encoding]::UTF8)
    Write-Output '音频转 MIDI 组件已就绪。'
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporary.StartsWith(([IO.Path]::GetFullPath($projectRoot) + '\'), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith('.audio-setup-') -and (Test-Path -LiteralPath $resolvedTemporary)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
