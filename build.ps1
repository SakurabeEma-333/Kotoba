# Build Kotoba (C# WinForms, .NET 8)
# Usage: .\build.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host ""
Write-Host "OK -> $PSScriptRoot\bin\Release\net8.0-windows\Kotoba.exe"
