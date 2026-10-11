<#
.SYNOPSIS
  Builds, tests, publishes and packages UpdateAll Win (Windows only).

.EXAMPLE
  ./build/build.ps1                                   # test + publish + MSI
  ./build/build.ps1 -SkipTests
  ./build/build.ps1 -CertificateThumbprint ABC123...  # also Authenticode-sign the exes/DLLs and the MSI

.NOTES
  Output: artifacts\publish (app + service) and artifacts\installer\UpdateAllWin-<version>-x64.msi
  Code signing uses signtool.exe from the Windows SDK with an RFC 3161 timestamp.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
Set-Location $root

function Invoke-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)" } }

if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    Invoke-Dotnet test tests/UpdateAllWin.Tests/UpdateAllWin.Tests.csproj -c $Configuration /p:Platform=x64
}

Write-Host '== Publish' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# App and service share one folder: both are self-contained .NET 10 x64 with the same runtime files.
Invoke-Dotnet publish src/UpdateAllWin.App/UpdateAllWin.App.csproj -c $Configuration /p:Platform=x64 -o $publish
Invoke-Dotnet publish src/UpdateAllWin.Service/UpdateAllWin.Service.csproj -c $Configuration /p:Platform=x64 -o $publish
# Without these the main window can't load its XAML and the app crashes on launch.
foreach ($required in 'UpdateAllWin.pri', 'App.xbf', 'MainWindow.xbf') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Publish output is missing $required" }
}

function Sign([string[]]$files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }
    & $signtool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'UpdateAll Win' $files
    if ($LASTEXITCODE -ne 0) { throw 'Code signing failed.' }
}

Write-Host '== Sign binaries' -ForegroundColor Cyan
Sign (Get-ChildItem $publish -Include 'UpdateAllWin.exe','UpdateAllService.exe','UpdateAllWin*.dll' -Recurse | ForEach-Object FullName)

Write-Host '== Installer' -ForegroundColor Cyan
Invoke-Dotnet build src/UpdateAllWin.Installer/UpdateAllWin.Installer.wixproj -c $Configuration "-p:PublishDir=$publish"
$msi = Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter *.msi -Recurse | Sort-Object LastWriteTime | Select-Object -Last 1
Sign @($msi.FullName)

Write-Host "Done: $($msi.FullName)" -ForegroundColor Green
