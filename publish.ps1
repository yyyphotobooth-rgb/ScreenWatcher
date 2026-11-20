# Builds the one-file version for friends: dist\ScreenWatcher.exe + dist\ScreenWatcher.exe.sig
# (self-contained Windows x64, no .NET needed on the target PC, no trimming). The .sig is for the
# in-app updater; it needs the private key in C:\ScreenWatcher-keys (or $env:SCREENWATCHER_SIGNING_KEY).
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File .\publish.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# Start from an empty dist\ so nothing old or personal is left next to the exe.
if (Test-Path $dist) { Remove-Item -Recurse -Force -LiteralPath $dist }

dotnet publish (Join-Path $root 'src\ScreenWatcher\ScreenWatcher.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=none -p:DebugSymbols=false `
    -p:ContinuousIntegrationBuild=true `
    -p:SatelliteResourceLanguages=en `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$files = Get-ChildItem -LiteralPath $dist -Recurse -File
if ($files.Count -ne 1 -or $files[0].Name -ne 'ScreenWatcher.exe') {
    throw "dist\ must hold only ScreenWatcher.exe, found: $($files.FullName -join ', ')"
}
$exe = $files[0]

# Sign for the in-app updater: dist\ScreenWatcher.exe.sig (ECDSA P-256). The private key stays
# OUTSIDE the repo (never printed or logged); the signer also checks the .sig against the public
# key built into the app, so a wrong key file fails here instead of on users' PCs.
$key = if ($env:SCREENWATCHER_SIGNING_KEY) { $env:SCREENWATCHER_SIGNING_KEY } else { 'C:\ScreenWatcher-keys\update-signing-private.pem' }
if (-not (Test-Path -LiteralPath $key)) { throw "Signing key not found: $key" }
dotnet run --project (Join-Path $root 'tools\UpdateSigner\UpdateSigner.csproj') -c Release -- sign $exe.FullName $key
if ($LASTEXITCODE -ne 0) { throw "Signing failed ($LASTEXITCODE)" }

foreach ($f in @($exe.FullName, "$($exe.FullName).sig")) {
    $i = Get-Item -LiteralPath $f
    '{0}  {1:N1} MB  SHA-256 {2}' -f $i.FullName, ($i.Length / 1MB), (Get-FileHash -Algorithm SHA256 -LiteralPath $i.FullName).Hash
}
