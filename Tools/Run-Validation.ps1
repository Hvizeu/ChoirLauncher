[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$settings = Join-Path $env:APPDATA 'songsofsyx\settings\LauncherSettings.txt'
$before = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }

$previousTestMode = $env:CHOIRLAUNCHER_TEST_MODE
$env:CHOIRLAUNCHER_TEST_MODE = '1'
try {
    & dotnet build (Join-Path $root 'ChoirLauncher.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project (Join-Path $root 'Tests\ChoirLauncher.Tests.csproj') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    $env:CHOIRLAUNCHER_TEST_MODE = $previousTestMode
}

$after = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
if ($before -ne $after) { throw 'Live LauncherSettings.txt changed during validation.' }
Write-Host "Live LauncherSettings unchanged: $after"
