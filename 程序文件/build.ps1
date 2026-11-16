param([string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$applicationRoot = if ((Split-Path -Leaf $projectRoot) -eq '程序文件') { Split-Path -Parent $projectRoot } else { $projectRoot }
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework C# compiler was not found.' }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$outputPath = if ([string]::IsNullOrWhiteSpace($Destination)) { Join-Path $applicationRoot '节奏口琴.exe' } else { [IO.Path]::GetFullPath($Destination) }
$manifestPath = Join-Path $projectRoot 'src\app.manifest'
$iconPath = Join-Path $projectRoot 'src\app.ico'
$resourceArguments = @()
foreach ($relativePath in @('data/catalog/pop909.json','data/catalog/POP909-LICENSE','scripts/audio-to-midi.py','scripts/audio-requirements.txt','scripts/setup-audio-runtime.ps1','scripts/compact-audio-runtime.py','runtime/webview2/Microsoft.Web.WebView2.Core.dll','runtime/webview2/Microsoft.Web.WebView2.WinForms.dll','runtime/webview2/runtimes/win-x64/native/WebView2Loader.dll','runtime/webview2/runtimes/win-x86/native/WebView2Loader.dll','runtime/webview2/LICENSE.txt')) {
    $resourcePath = Join-Path $projectRoot $relativePath
    $resourceArguments += "/resource:$resourcePath,HarmonicaContent/$relativePath"
}
$documentationRoot = Join-Path $applicationRoot '文档中心'
foreach ($item in Get-ChildItem -LiteralPath $documentationRoot -File -Recurse) {
    $relativePath = $item.FullName.Substring($documentationRoot.Length + 1).Replace('\','/')
    $resourceArguments += "/resource:$($item.FullName),HarmonicaDocs/$relativePath"
}
& $compilerPath /nologo /utf8output /target:winexe /platform:anycpu /optimize+ "/out:$outputPath" "/win32manifest:$manifestPath" "/win32icon:$iconPath" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Runtime.Serialization.dll /r:System.Security.dll $resourceArguments $sourceFiles
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Output "Built: $outputPath"
