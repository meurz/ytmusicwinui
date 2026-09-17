[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$CacheDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (!$CacheDirectory) { $CacheDirectory = Join-Path $repositoryRoot '.cache/core-v0.9.1' }
$releaseBase = 'https://github.com/meurz/youtube-music-core/releases/download/v0.9.1'
$artifacts = @{
    x64 = @{ Name = 'ytmusic-x86_64-pc-windows-msvc.tar.gz'; Sha256 = '16f10f6a806839d771a61916f454f6f7581ed128ab60aa14fbea1ad8a5750fde' }
    arm64 = @{ Name = 'ytmusic-aarch64-pc-windows-msvc.tar.gz'; Sha256 = '47ebfd7c913b01dc1daaed8e5520b6b71d1741a176b5d138cbceb3653d7d4045' }
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
Write-Host "Verified youtube-music-core v0.9.1 ($Architecture): $destination"
