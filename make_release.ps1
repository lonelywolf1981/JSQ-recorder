param([string]$Version = '0.2.4')

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$src     = Join-Path $root "JSQ\JSQ.UI.WPF\bin\Release\net48"
$outDir  = Join-Path $root "release"
$feedDir = Join-Path $outDir "feed"

New-Item -ItemType Directory -Force -Path $outDir  | Out-Null
New-Item -ItemType Directory -Force -Path $feedDir | Out-Null

Add-Type -Assembly 'System.IO.Compression.FileSystem'

$excludeFromUpdate = @(
    "data\experiments.db",
    "data\experiments.db-wal",
    "data\experiments.db-shm",
    "main_grid_layout.json",
    "app_settings.json"
)

function New-ZipFromDir {
    param(
        [string]$ZipPath,
        [string]$SourceDir,
        [string[]]$Exclude = @()
    )
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        Get-ChildItem -Path $SourceDir -Recurse -File | ForEach-Object {
            $rel     = $_.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
            $relNorm = $rel.Replace('/', '\')
            if ($Exclude -notcontains $relNorm) {
                $entryName = $rel.Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $_.FullName, $entryName, 'Optimal') | Out-Null
            }
        }
    } finally {
        $zip.Dispose()
    }
}

# 1. Update package
$updateZip = Join-Path $feedDir "update-$Version.zip"
New-ZipFromDir -ZipPath $updateZip -SourceDir $src -Exclude $excludeFromUpdate

$updateSize = (Get-Item $updateZip).Length
$sha256Obj  = [System.Security.Cryptography.SHA256]::Create()
$stream     = [System.IO.File]::OpenRead($updateZip)
$hashBytes  = $sha256Obj.ComputeHash($stream)
$stream.Close(); $sha256Obj.Dispose()
$sha256     = [System.BitConverter]::ToString($hashBytes).Replace('-', '')
Write-Host ("  [update]   " + [math]::Round($updateSize/1MB,2) + " MB  SHA256: " + $sha256)

# 2. Manifest
$notes = "v" + $Version + " - fix auto-update args quoting, UseShellExecute restart, per-post channel lock, System.Text.Json 9.0.0"
$manifest = [ordered]@{
    Version      = $Version
    PackageFile  = "update-$Version.zip"
    Sha256       = $sha256
    Size         = $updateSize
    Mandatory    = $false
    ReleaseNotes = $notes
    PublishedAt  = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')
}
$manifestJson = $manifest | ConvertTo-Json -Depth 3
$manifestPath = Join-Path $feedDir "manifest.json"
# UTF-8 без BOM — JsonSerializer корректно читает оба варианта, но без BOM надёжнее
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)
Write-Host ("  [manifest]  " + $manifestPath)

# 3. Installer
$setupZip = Join-Path $outDir ("JSQ-" + $Version + "-Setup.zip")
if (Test-Path $setupZip) { Remove-Item $setupZip -Force }
$zip2 = [System.IO.Compression.ZipFile]::Open($setupZip, 'Create')
try {
    Get-ChildItem -Path $src -Recurse -File | ForEach-Object {
        $rel       = $_.FullName.Substring($src.Length).TrimStart('\', '/')
        $entryName = $rel.Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip2, $_.FullName, $entryName, 'Optimal') | Out-Null
    }
    foreach ($ph in @('export/.gitkeep', 'logs/.gitkeep')) {
        $e = $zip2.CreateEntry($ph)
        $e.Open().Dispose()
    }
} finally {
    $zip2.Dispose()
}

$setupSize = (Get-Item $setupZip).Length
Write-Host ("  [installer] " + [math]::Round($setupSize/1MB,2) + " MB")

Write-Host ""
Write-Host ("Done. Artifacts in: " + $outDir)
Write-Host ""
Write-Host ("  Feed (copy to update server):  " + $feedDir)
Write-Host ("    manifest.json + update-" + $Version + ".zip")
Write-Host ""
Write-Host ("  Installer: JSQ-" + $Version + "-Setup.zip")
