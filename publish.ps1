# Builds dist\FocusStudy.exe: one self-contained file, no .NET install needed on the target PC.
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish FocusStudy.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Get-Item dist\FocusStudy.exe
"Built $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
