[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$CacheDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (!$CacheDirectory) { $CacheDirectory = Join-Path $repositoryRoot '.cache/core-v0.9.0' }
$releaseBase = 'https://github.com/meurz/youtube-music-core/releases/download/v0.9.0'
$artifacts = @{
    x64 = @{ Name = 'ytmusic-x86_64-pc-windows-msvc.tar.gz'; Sha256 = '5cc527b8bd9aecaa0432ac72fd9091c585cf9ef45d12abf58086d5254dc713b2' }
    arm64 = @{ Name = 'ytmusic-aarch64-pc-windows-msvc.tar.gz'; Sha256 = 'f49ff326b18506fe1e0675291c759f855b8d8e996c613b0606ace3c8de7563c6' }
}
$artifact = $artifacts[$Architecture]
New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
$archivePath = Join-Path $CacheDirectory $artifact.Name
$checksumPath = Join-Path $CacheDirectory 'SHA256SUMS'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (!(Test-Path $checksumPath)) {
    Invoke-WebRequest -UseBasicParsing -Uri "$releaseBase/SHA256SUMS" -OutFile $checksumPath
}
$publishedEntry = Get-Content $checksumPath | Where-Object { $_ -match ('^' + $artifact.Sha256 + '\s+\*?' + [regex]::Escape($artifact.Name) + '$') }
if (!$publishedEntry) { throw 'The release checksum manifest does not match the pinned archive hash.' }
if (!(Test-Path $archivePath)) {
    $downloadPath = "$archivePath.part"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$releaseBase/$($artifact.Name)" -OutFile $downloadPath
        if ((Get-FileHash $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.Sha256) {
            throw 'The downloaded native core failed SHA-256 verification.'
        }
        Move-Item -Force $downloadPath $archivePath
    } finally { if (Test-Path $downloadPath) { Remove-Item $downloadPath -Force } }
}
if ((Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.Sha256) {
    throw 'The cached native core failed SHA-256 verification. Remove the cached archive and retry.'
}
$destination = Join-Path $repositoryRoot ".native/win-$Architecture"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
# Extract only the verified release's DLL; never install its CLI or configuration.
& tar -xzf $archivePath -C $destination './youtube_music_core.dll'
if ($LASTEXITCODE -ne 0) { throw "Native core extraction failed ($LASTEXITCODE)." }
if (!(Test-Path (Join-Path $destination 'youtube_music_core.dll'))) { throw 'The native core DLL is absent from the release archive.' }
Write-Host "Verified youtube-music-core v0.9.0 ($Architecture): $destination"
