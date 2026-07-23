[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputRoot,
    [switch]$Force,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hostIsWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$hostIsLinux = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)
$hostIsMacOS = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)
$version = '0.3.0-rc4'
$buildId = 'choirlauncher-location-setup-20260723.1'
$project = [IO.Path]::Combine($root, 'Source', 'ChoirLauncher.Desktop', 'ChoirLauncher.Desktop.csproj')
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { [IO.Path]::Combine($root, 'Release', $version) } else { [IO.Path]::GetFullPath($OutputRoot) }
$workRoot = Join-Path $root "Release\.desktop-package-$RuntimeIdentifier-$version"
$publishRoot = Join-Path $workRoot 'publish'

function Assert-Command([string]$Name, [string[]]$Arguments) {
    & $Name @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Assert-SafeDevelopmentPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'Release')) + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($hostIsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $resolved.StartsWith($allowed, $comparison)) { throw "Unsafe generated-output path: $resolved" }
}

function Assert-NativePackagingHost {
    $valid = switch -Wildcard ($RuntimeIdentifier) {
        'win-*' { $hostIsWindows }
        'linux-*' { $hostIsLinux }
        'osx-*' { $hostIsMacOS }
        default { $false }
    }
    if (-not $valid) {
        throw "Build $RuntimeIdentifier on its native operating system so executable permissions and native bundle metadata are preserved."
    }
}

Assert-NativePackagingHost
Assert-SafeDevelopmentPath $workRoot
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$publishArguments = @(
    'publish', $project, '-c', 'Release', '-r', $RuntimeIdentifier,
    '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false',
    '-p:PublishSingleFile=false', '-o', $publishRoot
)
if ($NoRestore) { $publishArguments += '--no-restore' }
Assert-Command dotnet $publishArguments

Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $publishRoot
$dependencyInventory = [IO.Path]::Combine($root, 'Documentation', 'DEPENDENCY_LICENSE_INVENTORY.csv')
if (Test-Path -LiteralPath $dependencyInventory -PathType Leaf) {
    Copy-Item -LiteralPath $dependencyInventory -Destination $publishRoot
}
Copy-Item -LiteralPath (Join-Path $root 'Licenses') -Destination $publishRoot -Recurse

$core = Join-Path $publishRoot 'ChoirLauncher.Core.dll'
if (-not (Test-Path -LiteralPath $core -PathType Leaf)) { throw 'Published Core assembly is missing.' }
$coreText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($core))
if (-not $coreText.Contains($buildId)) { throw 'Published Core assembly does not contain the expected build ID.' }
if (Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object { $_.Extension -in @('.pdb', '.jar') -or $_.Name -match 'LauncherSettings|profile' }) {
    throw 'Forbidden debug, game, settings, or profile content found in the desktop package.'
}

$artifactName = "ChoirLauncher-Desktop-$RuntimeIdentifier-self-contained"
$artifactPath = if ($RuntimeIdentifier -eq 'linux-x64') {
    Join-Path $releaseRoot "$artifactName.tar.gz"
} else {
    Join-Path $releaseRoot "$artifactName.zip"
}
if (Test-Path -LiteralPath $artifactPath) {
    if (-not $Force) { throw "Package already exists. Re-run with -Force to replace it: $artifactPath" }
    Remove-Item -LiteralPath $artifactPath -Force
}

if ($RuntimeIdentifier -like 'osx-*') {
    $appRoot = Join-Path $workRoot 'ChoirLauncher.app'
    $macOs = [IO.Path]::Combine($appRoot, 'Contents', 'MacOS')
    $resources = [IO.Path]::Combine($appRoot, 'Contents', 'Resources')
    New-Item -ItemType Directory -Path $macOs -Force | Out-Null
    New-Item -ItemType Directory -Path $resources -Force | Out-Null
    Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination $macOs }
    $iconSource = [IO.Path]::Combine($root, 'Source', 'ChoirLauncher.Desktop', 'Assets', 'SongsOfSyxIcon64.png')
    $iconSet = Join-Path $workRoot 'ChoirLauncher.iconset'
    New-Item -ItemType Directory -Path $iconSet -Force | Out-Null
    foreach ($icon in @(
        @('16', 'icon_16x16.png'), @('32', 'icon_16x16@2x.png'),
        @('32', 'icon_32x32.png'), @('64', 'icon_32x32@2x.png'),
        @('128', 'icon_128x128.png'), @('256', 'icon_128x128@2x.png'),
        @('256', 'icon_256x256.png'), @('512', 'icon_256x256@2x.png'),
        @('512', 'icon_512x512.png'), @('1024', 'icon_512x512@2x.png')
    )) {
        Assert-Command sips @('-z', $icon[0], $icon[0], $iconSource, '--out', (Join-Path $iconSet $icon[1]))
    }
    Assert-Command iconutil @('-c', 'icns', $iconSet, '-o', (Join-Path $resources 'ChoirLauncher.icns'))
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDevelopmentRegion</key><string>English</string>
  <key>CFBundleDisplayName</key><string>ChoirLauncher</string>
  <key>CFBundleExecutable</key><string>ChoirLauncher</string>
  <key>CFBundleIdentifier</key><string>com.henrique.choirlauncher</string>
  <key>CFBundleIconFile</key><string>ChoirLauncher.icns</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>ChoirLauncher</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
    [IO.File]::WriteAllText((Join-Path $appRoot 'Contents\Info.plist'), $plist, (New-Object Text.UTF8Encoding($false)))
    Assert-Command chmod @('+x', (Join-Path $macOs 'ChoirLauncher'))
    Push-Location $workRoot
    try { Assert-Command ditto @('-c', '-k', '--sequesterRsrc', '--keepParent', 'ChoirLauncher.app', $artifactPath) }
    finally { Pop-Location }
} elseif ($RuntimeIdentifier -eq 'linux-x64') {
    Assert-Command chmod @('+x', (Join-Path $publishRoot 'ChoirLauncher'))
    Push-Location $publishRoot
    try { Assert-Command tar @('-czf', $artifactPath, '.') }
    finally { Pop-Location }
} else {
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $artifactPath -CompressionLevel Optimal
}

$result = [ordered]@{
    schema = 'choirlauncher.desktop-package.v1'
    version = $version
    buildId = $buildId
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    file = [IO.Path]::GetFileName($artifactPath)
    bytes = (Get-Item -LiteralPath $artifactPath).Length
    sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifestPath = Join-Path $releaseRoot "$artifactName.manifest.json"
[IO.File]::WriteAllText($manifestPath, ($result | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
Remove-Item -LiteralPath $workRoot -Recurse -Force
$result | ConvertTo-Json -Depth 4
