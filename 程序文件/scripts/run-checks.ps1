param([string]$ApplicationPath, [switch]$PackagingOnly, [switch]$SongFlowOnly, [switch]$WaterfallOnly, [switch]$DetailsOnly, [switch]$InstallerOnly, [switch]$WithoutForeground)
$ErrorActionPreference = 'Stop'
if ((@($PackagingOnly, $SongFlowOnly, $WaterfallOnly, $DetailsOnly, $InstallerOnly, $WithoutForeground) | Where-Object { $_ }).Count -gt 1) { throw '请选择一类专项检查。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$applicationRoot = if ((Split-Path -Leaf $projectRoot) -eq '程序文件') { Split-Path -Parent $projectRoot } else { $projectRoot }
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$checkDirectory = Join-Path $temporaryRoot ('HarmonicaStudio-checks-' + [Guid]::NewGuid().ToString('N'))
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path (Join-Path $checkDirectory 'data\catalog') -Force | Out-Null
try {
    $previousApplication = $env:HARMONICA_TEST_APPLICATION
    $env:HARMONICA_TEST_APPLICATION = if ($ApplicationPath) { [IO.Path]::GetFullPath($ApplicationPath) } else { Join-Path $applicationRoot '节奏口琴.exe' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'data\catalog\pop909.json') -Destination (Join-Path $checkDirectory 'data\catalog\pop909.json')
    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
    $testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    $testProgram = Join-Path $checkDirectory 'AllTests.exe'
    $manifestPath = Join-Path $projectRoot 'src\app.manifest'
    & $compilerPath /nologo /utf8output /target:exe /main:AllTests "/out:$testProgram" "/win32manifest:$manifestPath" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Security.dll $sourceFiles $testFiles
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    if ($PackagingOnly) { & $testProgram '--packaging-only' } elseif ($SongFlowOnly) { & $testProgram '--song-flow-only' } elseif ($WaterfallOnly) { & $testProgram '--waterfall-only' } elseif ($DetailsOnly) { & $testProgram '--details-only' } elseif ($InstallerOnly) { & $testProgram '--installer-only' } elseif ($WithoutForeground) { & $testProgram '--without-foreground' } else { & $testProgram }
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    $env:HARMONICA_TEST_APPLICATION = $previousApplication
    $resolvedDirectory = [IO.Path]::GetFullPath($checkDirectory)
    if ($resolvedDirectory.StartsWith(($temporaryRoot + '\'), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedDirectory).StartsWith('HarmonicaStudio-checks-') -and (Test-Path -LiteralPath $resolvedDirectory)) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
