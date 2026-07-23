# ChoirLauncher

[![Build](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml/badge.svg)](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml)

ChoirLauncher is an independent Windows, Linux, and macOS mod-profile manager and alternative
launcher for **Songs of Syx**. It detects the installed game version directly from
the game JAR, discovers local and Workshop mods,
maintains reusable profiles, explains dependencies and conflicts, safely writes the
official mod configuration, exposes the game's launcher settings, and starts the
game through structurally validated targets. Checksums identify known builds and
support diagnostics; an unrecognized checksum does not block launch.

ChoirLauncher is not affiliated with or endorsed by the Songs of Syx developers.
It does not include the game.

## Highlights

- local and Steam Workshop mod discovery;
- named profiles with enable state and deterministic priority;
- guided import of profile JSON or saved official `LauncherSettings.txt` mod lists;
- drag-and-drop ordering, search, filters, undo, and redo;
- dependency, incompatibility, duplicate-ID, class-shadow, and data-overlap checks scoped strictly to enabled mods;
- per-mod conflict explanations and suggested ordering;
- guarded preview, backup, atomic apply, verification, and restore;
- integrated Songs of Syx launcher settings, language selection, and system info;
- scroll-contained compatibility warnings that keep the acknowledgement button visible;
- version-aware direct launch or official-launcher handoff without a fixed checksum gate;
- hash-approved Java-agent launch support for mods that need pre-start instrumentation;
- route-aware Java-agent handling that ignores inactive persistent `JVM_ARGS2` entries on direct Linux/macOS launch;
- safe texture-cache invalidation for agent-backed mods that need Songs of Syx to rebuild its sprite atlas;
- Steam registry, library, and app-manifest game discovery with a clear manual folder picker when automatic discovery fails;
- reviewable setup locations with editable paths, validation, Browse, and Auto-detect recovery;
- native Steam and user-data discovery on Windows, Linux, and macOS;
- PE, ELF, and Mach-O launch validation with native game routes for each OS;
- bounded GitHub Releases update checks with Preview / RC and Stable channels;
- a Windows per-user installer plus self-contained Windows, Linux, Intel Mac, and Apple Silicon Mac packages.

Priority `1` is the **lowest** priority. Larger numbers are higher priority. The
launcher reverses the enabled profile exactly once when writing Songs of Syx's
highest-first official `MODS` array.

## Current release

Current release candidate: `0.3.0-rc4`

Download the package for your operating system and processor from
[GitHub Releases](https://github.com/Hvizeu/ChoirLauncher/releases).

Every package is self-contained: users do not need to install .NET separately.
Windows users can use the setup program or portable ZIP. Linux users receive an
x64 `.tar.gz`. macOS users should choose `osx-arm64` for Apple Silicon or
`osx-x64` for an Intel Mac.

The Windows setup installs by default to:

```text
%APPDATA%\songsofsyx\ChoirLauncher
```

and stores profiles, logs, preferences, and backups under:

```text
%LOCALAPPDATA%\ChoirLauncher
```

Windows setup shows editable ChoirLauncher and Songs of Syx folders before
installation. **Browse** selects a different location, **Auto-detect** reruns Steam
discovery, and an invalid **Continue** attempt remains on the same setup window
with a clear error. Portable Windows, Linux, and macOS builds provide equivalent
first-run game-location confirmation. The validated choice is retained in the
platform-native ChoirLauncher data folder and does not need to be confirmed again
while it remains valid.

Setup and the installed launcher carry explicit Windows 10/11 compatibility
manifests and the same native icon used by the shortcut, title bar, dialogs, and
taskbar. Setup also refreshes the shortcut with Windows Shell after an upgrade.

See the [step-by-step Windows, Linux, and macOS installation guide](docs/INSTALLATION.md).
See [game-build compatibility](docs/COMPATIBILITY.md) for the version and checksum policy.

## Safety model

ChoirLauncher treats every mod and archive as untrusted input. It inventories ZIP
and JAR structures without loading mod classes. Profile application:

1. previews the exact change;
2. checks for running game/launcher processes;
3. obtains a cross-process configuration lock;
4. verifies that the preview is not stale;
5. creates an exact backup;
6. writes through a same-filesystem temporary file;
7. reparses and hashes the result;
8. rolls back if verification fails.

Unknown official settings are preserved. The game JAR and executables are never
modified. ChoirLauncher has no telemetry. Its only network behavior is the
documented, bounded, read-only GitHub Releases update check. Startup checks are
user-configurable and throttled to once per day; opening a release asset or page
requires explicit user action.

## Building

Requirements: Git LFS and the .NET 8 SDK. The main solution builds on Windows,
Linux, and macOS. The Windows installer project remains Windows-only.

```powershell
git lfs install
git clone https://github.com/Hvizeu/ChoirLauncher.git
cd ChoirLauncher
dotnet build .\ChoirLauncher.sln -c Release
```

Native self-contained packages are built with:

```powershell
pwsh ./Tools/Build-DesktopPackage.ps1 -RuntimeIdentifier win-x64
pwsh ./Tools/Build-DesktopPackage.ps1 -RuntimeIdentifier linux-x64
pwsh ./Tools/Build-DesktopPackage.ps1 -RuntimeIdentifier osx-x64
pwsh ./Tools/Build-DesktopPackage.ps1 -RuntimeIdentifier osx-arm64
```

Run each package build on its matching native operating system. GitHub Actions
verifies the source on native Windows, Linux, Intel macOS, and Apple Silicon macOS
runners and creates the Linux/macOS release archives there.

Official self-contained installers and portable ZIPs are built from the private
authoritative development project and published under
[GitHub Releases](https://github.com/Hvizeu/ChoirLauncher/releases).

## Contributing

Bug reports, GitHub forks made for contribution work, and pull requests are
welcome. Do not commit generated binaries, release archives, private Songs of Syx
data, runtime logs, or installed-game files. Read
[CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Maintainers run
the private validation and release gates before accepting a public change.

## License

ChoirLauncher is distributed under the
[ChoirLauncher Source-Available Contribution License 1.0](LICENSE). You may use
official releases, inspect and privately modify the source, create a GitHub fork
for contribution work, and submit updates. Independent redistribution,
re-hosting, binary releases from forks, removal of attribution, and claims of
original authorship are prohibited without written authorization.

This is a source-available project, not an OSI open-source project.

## Compatibility and limitations

- Launch recognizes the installed version from `game/VERSION.class` and does not
  reject legitimate patches or older builds solely because their hashes are new.
- Mod analysis and the integrated game-settings editor are currently validated
  against v71.44. On another version, launch remains available while an
  incompatible settings schema fails without writing.
- The Windows setup is unsigned and macOS packages are not notarized; verify
  release checksums and follow the per-application approval steps in the install guide.
- Linux and macOS support remains under community runtime QA. RC2 addresses the
  first macOS report involving duplicate enabled/disabled agent-backed development copies.
- Automatic download-and-replace updates are not enabled. RC3 checks for releases
  and opens the matching HTTPS download only after explicit user action.
- The project owner has confirmed permission to include the current artwork in
  official ChoirLauncher source and binary distributions. That permission does
  not authorize independent redistribution outside the project license.

Third-party notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
