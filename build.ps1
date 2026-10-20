<#
.SYNOPSIS
    Builds Connection Hub and packages it as an MSI installer.
.EXAMPLE
    .\build.ps1                 # version 1.0.0
    .\build.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
$msiOut = Join-Path $root "artifacts\msi"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "==> Publishing application ($Version)" -ForegroundColor Cyan
dotnet publish "$root\src\ConnectionHub\ConnectionHub.csproj" -c $Configuration -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

& "$root\build\Verify-Publish.ps1" -PublishDir $publish

Write-Host "==> Building MSI" -ForegroundColor Cyan
dotnet build "$root\installer\ConnectionHub.Installer.wixproj" -c $Configuration -p:ProductVersion=$Version -p:AppPublishDir="$publish\" -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$msi = Get-ChildItem $msiOut -Filter *.msi -Recurse | Select-Object -First 1
Write-Host "==> Done: $($msi.FullName)" -ForegroundColor Green
