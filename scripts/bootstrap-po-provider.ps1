[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$cacheRoot = Join-Path $repositoryRoot '.cache/po-provider'
$sourceDirectory = Join-Path $repositoryRoot 'tools/po-provider'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot ".native/win-$Architecture/po-provider" }
$nodeVersion = '22.22.3'
$nodeHashes = @{
    x64 = '6c8d54f635feff4df76c2ca80f45332eb2ff57d25226edce36592e51a177ee33'
    arm64 = '00be129a09e8872cd52d3bb8bba12412c5733d2224123a482a2dca4a6fbf2586'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
function Get-NodeRuntime([string]$RuntimeArchitecture) {
    $name = "node-v$nodeVersion-win-$RuntimeArchitecture"
    $archive = Join-Path $cacheRoot "$name.zip"
    if (!(Test-Path $archive)) {
        $temporary = "$archive.part"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/v$nodeVersion/$name.zip" -OutFile $temporary
            if ((Get-FileHash $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nodeHashes[$RuntimeArchitecture]) {
                throw 'The downloaded Node.js runtime failed SHA-256 verification.'
            }
            Move-Item $temporary $archive -Force
        } finally { if (Test-Path $temporary) { Remove-Item $temporary -Force } }
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nodeHashes[$RuntimeArchitecture]) {
        throw 'The cached Node.js runtime failed SHA-256 verification.'
    }
    $directory = Join-Path $cacheRoot $name
    if (!(Test-Path (Join-Path $directory 'node.exe'))) { Expand-Archive -Path $archive -DestinationPath $cacheRoot -Force }
    return $directory
}
$hostArchitecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') { 'arm64' } else { 'x64' }
$hostRuntime = Get-NodeRuntime $hostArchitecture
$targetRuntime = if ($Architecture -eq $hostArchitecture) { $hostRuntime } else { Get-NodeRuntime $Architecture }
$hostNode = Join-Path $hostRuntime 'node.exe'
$npmCli = Join-Path $hostRuntime 'node_modules/npm/bin/npm-cli.js'
$buildDirectory = Join-Path $cacheRoot "build-$Architecture"
$npmCache = Join-Path $cacheRoot 'npm-cache'
New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
foreach ($file in @('package.json', 'package-lock.json', 'worker.cjs', 'build.mjs', 'README.md', 'LICENSE')) {
    Copy-Item (Join-Path $sourceDirectory $file) $buildDirectory -Force
}
Copy-Item (Join-Path $sourceDirectory 'vendor') $buildDirectory -Recurse -Force
& $hostNode $npmCli ci --legacy-peer-deps --ignore-scripts --no-audit --no-fund --cache $npmCache --prefix $buildDirectory "--cpu=$hostArchitecture" --os=win32
if ($LASTEXITCODE -ne 0) { throw "PO helper build dependency restore failed ($LASTEXITCODE)." }
Push-Location $buildDirectory
try { & $hostNode build.mjs; if ($LASTEXITCODE -ne 0) { throw "PO helper bundling failed ($LASTEXITCODE)." } }
finally { Pop-Location }
& $hostNode $npmCli ci --omit=dev --legacy-peer-deps --ignore-scripts --no-audit --no-fund --cache $npmCache --prefix $buildDirectory "--cpu=$Architecture" --os=win32
if ($LASTEXITCODE -ne 0) { throw "PO helper runtime dependency restore failed ($LASTEXITCODE)." }
$canvasPackage = Join-Path $buildDirectory "node_modules/@napi-rs/canvas-win32-$Architecture-msvc"
if (!(Test-Path $canvasPackage)) { throw 'The matching native canvas package is absent.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('package.json', 'package-lock.json', 'worker.cjs', 'build.mjs', 'README.md', 'LICENSE', 'vendor', 'dist', 'node_modules')) {
    Copy-Item (Join-Path $buildDirectory $name) $OutputDirectory -Recurse -Force
}
Copy-Item (Join-Path $targetRuntime 'node.exe') $OutputDirectory -Force
Copy-Item (Join-Path $targetRuntime 'LICENSE') (Join-Path $OutputDirectory 'NODE-LICENSE') -Force
if ($Architecture -eq $hostArchitecture) {
    & (Join-Path $OutputDirectory 'node.exe') (Join-Path $OutputDirectory 'worker.cjs') --check-runtime
    if ($LASTEXITCODE -ne 0) { throw "PO helper DOM/canvas verification failed ($LASTEXITCODE)." }
}
Write-Host "Packaged native PO helper ($Architecture): $OutputDirectory"
