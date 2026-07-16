[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$buildInfo = Get-Content -LiteralPath (Join-Path $root 'Source\ChoirLauncher.Core\BuildInfo.cs') -Raw
$versionMatch = [regex]::Match($buildInfo, 'Version\s*=\s*"([^"]+)"')
$buildMatch = [regex]::Match($buildInfo, 'BuildId\s*=\s*"([^"]+)"')
if (-not $versionMatch.Success -or -not $buildMatch.Success) { throw 'Could not read version/build ID from BuildInfo.cs.' }
$version = $versionMatch.Groups[1].Value
$buildId = $buildMatch.Groups[1].Value
$releaseRoot = Join-Path $root 'Release'
$release = Join-Path $releaseRoot $version
$staging = Join-Path $releaseRoot ".staging-$version"
$settings = Join-Path $env:APPDATA 'songsofsyx\settings\LauncherSettings.txt'

function Get-HashOrMissing([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return 'missing'
}

function Assert-Command([string]$Name, [string[]]$Arguments) {
    & $Name @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Assert-WaitingExecutable([string]$Path, [string[]]$Arguments, [string]$Failure) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "$Failure Exit code: $($process.ExitCode)." }
}

function Assert-SafeGeneratedPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe generated path: $resolved"
    }
}

function Assert-ReleaseHashes {
    $manifest = Join-Path $release 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifest)) { throw 'Release checksum manifest is missing.' }
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '  ', 2
        if ($parts.Count -ne 2) { throw "Malformed checksum line: $line" }
        $target = Join-Path $release $parts[1]
        if ((Get-HashOrMissing $target) -ne $parts[0]) { throw "Release hash mismatch: $($parts[1])" }
    }
    Write-Host "Verified release hashes: $release"
}

if ($VerifyOnly) {
    Assert-ReleaseHashes
    exit 0
}

$settingsBefore = Get-HashOrMissing $settings
if (Test-Path -LiteralPath $release) {
    if (-not $Force) { throw "Release exists. Re-run with -Force to replace generated output: $release" }
    Assert-SafeGeneratedPath $release
    Remove-Item -LiteralPath $release -Recurse -Force
}
if (Test-Path -LiteralPath $staging) {
    Assert-SafeGeneratedPath $staging
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $release -Force | Out-Null
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$previousTestMode = $env:CHOIRLAUNCHER_TEST_MODE
$env:CHOIRLAUNCHER_TEST_MODE = '1'
try {
    Assert-Command dotnet @('build', (Join-Path $root 'ChoirLauncher.sln'), '-c', 'Debug')
    Assert-Command dotnet @('run', '--project', (Join-Path $root 'Tests\ChoirLauncher.Tests.csproj'), '-c', 'Debug', '--no-build')
    Assert-Command dotnet @('build', (Join-Path $root 'ChoirLauncher.sln'), '-c', 'Release')
    Assert-Command dotnet @('run', '--project', (Join-Path $root 'Tests\ChoirLauncher.Tests.csproj'), '-c', 'Release', '--no-build')

    $desktop = Join-Path $staging 'ChoirLauncher-Desktop-win-x64-self-contained'
    Assert-Command dotnet @(
        'publish', (Join-Path $root 'Source\ChoirLauncher.Desktop\ChoirLauncher.Desktop.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $desktop
    )
    Get-ChildItem -LiteralPath $desktop -Recurse -File -Filter '*.pdb' | Remove-Item -Force

    $core = Join-Path $desktop 'ChoirLauncher.Core.dll'
    if (-not (Test-Path -LiteralPath $core)) { throw 'Published Core assembly is missing.' }
    $coreText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($core))
    if (-not $coreText.Contains($buildId)) { throw 'Published Core assembly does not contain the expected build ID.' }

    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $desktop
    Copy-Item -LiteralPath (Join-Path $root 'Licenses') -Destination $desktop -Recurse
    $payloadManifest = [ordered]@{
        schema = 'choirlauncher.installer-payload.v1'
        version = $version
        buildId = $buildId
        entrypoint = 'ChoirLauncher.exe'
        target = 'win-x64-self-contained'
    }
    [IO.File]::WriteAllText(
        (Join-Path $desktop 'installer-payload.json'),
        ($payloadManifest | ConvertTo-Json -Depth 4),
        (New-Object Text.UTF8Encoding($false)))

    $fixedTimestamp = [DateTime]::SpecifyKind([DateTime]'2000-01-01T00:00:00', [DateTimeKind]::Utc)
    Get-ChildItem -LiteralPath $desktop -Recurse -Force | ForEach-Object { $_.LastWriteTimeUtc = $fixedTimestamp }
    $payloadZip = Join-Path $release 'ChoirLauncher-Desktop-win-x64-self-contained.zip'
    Compress-Archive -Path (Join-Path $desktop '*') -DestinationPath $payloadZip -CompressionLevel Optimal
    $payloadHash = (Get-FileHash -LiteralPath $payloadZip -Algorithm SHA256).Hash.ToLowerInvariant()

    $installerOutput = Join-Path $staging 'ChoirLauncher-Installer-win-x64-self-contained'
    Assert-Command dotnet @(
        'publish', (Join-Path $root 'Source\ChoirLauncher.Installer\ChoirLauncher.Installer.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:PayloadZip=$payloadZip", "-p:PayloadSha256=$payloadHash",
        "-p:PayloadVersion=$version", "-p:PayloadBuildId=$buildId",
        '-o', $installerOutput
    )
    $installer = Join-Path $installerOutput 'ChoirLauncher-Setup.exe'
    if (-not (Test-Path -LiteralPath $installer)) { throw 'Setup executable was not produced.' }
    Assert-WaitingExecutable $installer @('--verify') 'Embedded setup payload verification failed.'

    $smoke = Join-Path $staging 'installer-smoke-install'
    $result = Join-Path $staging 'installer-smoke-result.txt'
    Assert-WaitingExecutable $installer @('--silent','--no-shortcut','--install-root',$smoke,'--result-file',$result) 'Clean-install smoke test failed.'
    Assert-WaitingExecutable $installer @('--silent','--no-shortcut','--install-root',$smoke,'--result-file',$result) 'Upgrade smoke test failed.'
    $installed = Get-Content -LiteralPath (Join-Path $smoke 'installed-release.json') -Raw | ConvertFrom-Json
    if ($installed.version -ne $version -or $installed.buildId -ne $buildId -or $installed.payloadSha256 -ne $payloadHash) {
        throw 'Installed smoke-test identity does not match the release.'
    }
    Copy-Item -LiteralPath $installer -Destination (Join-Path $release 'ChoirLauncher-Setup-win-x64.exe')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $release

    $artifacts = Get-ChildItem -LiteralPath $release -File | Where-Object { $_.Extension -in @('.zip','.exe') } | Sort-Object Name | ForEach-Object {
        [ordered]@{
            file = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schema = 'choirlauncher.public-release.v1'
        version = $version
        buildId = $buildId
        target = 'Windows x64 / Songs of Syx 0.71.44'
        repository = 'https://github.com/Hvizeu/ChoirLauncher'
        artifacts = @($artifacts)
        tests = 224
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        (Join-Path $release 'release-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 6),
        (New-Object Text.UTF8Encoding($false)))

    $checksumLines = Get-ChildItem -LiteralPath $release -Recurse -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($release.Length + 1).Replace('\','/')
            '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
        }
    [IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $checksumLines, (New-Object Text.UTF8Encoding($false)))

    $releaseText = Get-ChildItem -LiteralPath $release -Recurse -File |
        Where-Object { $_.Extension -in @('.md','.json','.txt','.csv') }
    if ($releaseText | Select-String -Pattern '[A-Za-z]:\\Users\\' -ErrorAction SilentlyContinue) {
        throw 'Private absolute path found in release text.'
    }
    Assert-ReleaseHashes
}
finally {
    $env:CHOIRLAUNCHER_TEST_MODE = $previousTestMode
    if (Test-Path -LiteralPath $staging) {
        Assert-SafeGeneratedPath $staging
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

$settingsAfter = Get-HashOrMissing $settings
if ($settingsBefore -ne $settingsAfter) { throw 'Live LauncherSettings.txt changed during build/validation.' }
Write-Host "Release: $release"
Write-Host "Build ID: $buildId"
Write-Host "Live LauncherSettings unchanged: $settingsAfter"
