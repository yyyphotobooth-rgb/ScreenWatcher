# Builds the one-file version for friends: dist\ScreenWatcher.exe
# (self-contained Windows x64, no .NET needed on the target PC, no trimming).
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
'{0}  {1:N1} MB  SHA-256 {2}' -f $exe.FullName, ($exe.Length / 1MB), (Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName).Hash
