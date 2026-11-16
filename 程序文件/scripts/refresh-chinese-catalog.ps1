param(
    [string]$SourceXlsx,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $projectPath 'data\catalog\pop909.json' }
$indexUrl = 'https://raw.githubusercontent.com/music-x-lab/POP909-Dataset/master/POP909/index.xlsx'
$sourceUrl = 'https://github.com/music-x-lab/POP909-Dataset'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Only the small metadata workbook is fetched. No song/audio collection is downloaded.
if ($SourceXlsx) {
    $fileInfo = Get-Item -LiteralPath $SourceXlsx
    if ($fileInfo.Length -gt 1MB) { throw 'Metadata workbook exceeds 1 MB.' }
    $workbookBytes = [IO.File]::ReadAllBytes($fileInfo.FullName)
} else {
    $request = [Net.HttpWebRequest]::Create($indexUrl)
    $request.UserAgent = 'RhythmHarmonica/1.1 (.NET Framework)'
    $request.Timeout = 12000
    $request.ReadWriteTimeout = 12000
    $request.AllowAutoRedirect = $false
    $response = $request.GetResponse()
    try {
        if ([int]$response.StatusCode -ne 200 -or $response.ContentLength -gt 1MB) { throw 'Metadata download failed or exceeds 1 MB.' }
        $networkStream = $response.GetResponseStream()
        $memoryStream = New-Object IO.MemoryStream
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        try {
            $chunk = New-Object byte[] 8192
            while ($true) {
                $remaining = 12000 - [int]$elapsed.ElapsedMilliseconds
                if ($remaining -le 0) { $request.Abort(); throw 'Metadata download timed out.' }
                if ($networkStream.CanTimeout) { $networkStream.ReadTimeout = $remaining }
                $read = $networkStream.Read($chunk, 0, $chunk.Length)
                if ($read -eq 0) { break }
                if ($memoryStream.Length + $read -gt 1MB) { throw 'Metadata workbook exceeds 1 MB.' }
                $memoryStream.Write($chunk, 0, $read)
            }
            $workbookBytes = $memoryStream.ToArray()
        } finally { $networkStream.Dispose(); $memoryStream.Dispose() }
    } finally { $response.Dispose() }
}

$workbookStream = New-Object IO.MemoryStream(,$workbookBytes)
$archive = New-Object IO.Compression.ZipArchive($workbookStream, [IO.Compression.ZipArchiveMode]::Read, $false)
function Read-WorkbookXml([string]$Name) {
    $entry = $archive.GetEntry($Name)
    if (-not $entry) { return $null }
    if ($entry.Length -gt 5MB) { throw 'Expanded metadata exceeds 5 MB.' }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 5MB
    $stream = $entry.Open()
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose(); $stream.Dispose() }
}

try {
    $shared = Read-WorkbookXml 'xl/sharedStrings.xml'
    $sharedStrings = New-Object 'Collections.Generic.List[string]'
    if ($shared) {
        foreach ($item in $shared.SelectNodes('//*[local-name()="si"]')) {
            $sharedStrings.Add(($item.SelectNodes('.//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '')
        }
    }
    $sheet = Read-WorkbookXml 'xl/worksheets/sheet1.xml'
    if (-not $sheet) { throw 'Metadata worksheet was not found.' }
    $rows = @($sheet.SelectNodes('//*[local-name()="sheetData"]/*[local-name()="row"]'))
    $headers = @{}
    $songs = New-Object 'Collections.Generic.List[object]'
    $seenIds = New-Object 'Collections.Generic.HashSet[string]'
    foreach ($row in $rows) {
        $cells = @{}
        foreach ($cell in $row.SelectNodes('./*[local-name()="c"]')) {
            $column = $cell.GetAttribute('r') -replace '\d+$', ''
            $type = $cell.GetAttribute('t')
            $valueNode = $cell.SelectSingleNode('./*[local-name()="v"]')
            $value = if ($valueNode) { $valueNode.InnerText } else { '' }
            if ($type -eq 's') { $value = $sharedStrings[[int]$value] }
            if ($type -eq 'inlineStr') { $value = ($cell.SelectNodes('.//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '' }
            $cells[$column] = [string]$value
        }
        if ($headers.Count -eq 0) {
            foreach ($column in $cells.Keys) { $headers[$cells[$column]] = $column }
            if (-not $headers.ContainsKey('song_id') -or -not $headers.ContainsKey('name') -or -not $headers.ContainsKey('artist')) { throw 'Unexpected metadata columns.' }
            continue
        }
        $idNumber = [int]$cells[$headers['song_id']]
        $id = $idNumber.ToString('000', [Globalization.CultureInfo]::InvariantCulture)
        $title = $cells[$headers['name']]
        $artist = $cells[$headers['artist']]
        if ($idNumber -lt 1 -or $idNumber -gt 909 -or -not $seenIds.Add($id) -or [string]::IsNullOrWhiteSpace($title)) { throw 'Invalid or duplicate song metadata.' }
        $songs.Add([pscustomobject][ordered]@{ id = $id; title = $title; artist = $artist })
    }
    if ($songs.Count -ne 909) { throw ('Expected 909 songs; found ' + $songs.Count + '.') }
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $sha256 = ([BitConverter]::ToString($digest.ComputeHash($workbookBytes))).Replace('-', '').ToLowerInvariant() } finally { $digest.Dispose() }
    $index = [ordered]@{
        source = $sourceUrl
        indexUrl = $indexUrl
        licenseUrl = $sourceUrl + '/blob/master/LICENSE'
        sourceLicenseLabel = 'POP909 · 仓库标注 MIT'
        copyright = 'Copyright (c) 2020 Music X Lab'
        checkedDate = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        indexSha256 = $sha256
        songs = $songs.ToArray()
    }
    $outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($index | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Output ('Updated metadata: ' + $songs.Count + ' songs; ' + [IO.Path]::GetFullPath($OutputPath))
} finally { $archive.Dispose(); $workbookStream.Dispose() }
