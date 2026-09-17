[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repositoryRoot 'src/Music.Desktop/Music.Desktop.csproj'
if (!(Test-Path (Join-Path $repositoryRoot 'external/core/examples/dotnet/YouTubeMusic.Interop.csproj'))) {
    throw 'The core submodule is missing. Run: git submodule update --init --recursive'
}
& (Join-Path $PSScriptRoot 'bootstrap-core.ps1') -Architecture $Architecture
$runtimeId = "win-$Architecture"
$platformName = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
& dotnet restore $projectPath -r $runtimeId "-p:Platform=$platformName"
if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }
& dotnet build $projectPath -c $Configuration -r $runtimeId "-p:Platform=$platformName" --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
if ($Publish) {
    if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot "artifacts/ytmusicwinui-$runtimeId" }
    & dotnet publish $projectPath -c $Configuration -r $runtimeId "-p:Platform=$platformName" --no-restore --no-build -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    foreach ($required in @('ytmusicwinui.exe', 'ytmusicwinui.pri', 'youtube_music_core.dll', 'Microsoft.UI.Xaml.dll')) {
        if (!(Test-Path (Join-Path $OutputDirectory $required))) { throw "Published output is missing $required." }
    }
    Copy-Item (Join-Path $repositoryRoot 'LICENSE') $OutputDirectory
    Copy-Item (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') $OutputDirectory
    Write-Host "Published: $OutputDirectory/ytmusicwinui.exe"
}
