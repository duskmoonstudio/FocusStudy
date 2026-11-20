# Builds a single self-contained FocusStudy exe: no .NET install needed on the target PC.
# Usage:
#   powershell -ExecutionPolicy Bypass -File publish.ps1                      # Intel/AMD 64-bit -> dist\FocusStudy.exe
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -Runtime win-arm64   # ARM64 -> dist-arm64\FocusStudy.exe
param(
  [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
  [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = if ($Runtime -eq 'win-x64') { 'dist' } else { 'dist-' + $Runtime.Substring(4) }

dotnet publish FocusStudy.csproj -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Get-Item (Join-Path $out 'FocusStudy.exe')
"Built $($exe.FullName) for $Runtime ($([math]::Round($exe.Length / 1MB, 1)) MB)"
