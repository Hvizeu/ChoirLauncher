# ChoirLauncher

[![Build](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml/badge.svg)](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml)

ChoirLauncher is an independent Windows mod-profile manager and alternative
launcher for **Songs of Syx 0.71.44**. It discovers local and Workshop mods,
maintains reusable profiles, explains dependencies and conflicts, safely writes the
official mod configuration, exposes the game's launcher settings, and starts the
game through fingerprint-verified targets.

ChoirLauncher is not affiliated with or endorsed by the Songs of Syx developers.
It does not include the game.

## Highlights

- local and Steam Workshop mod discovery;
- named profiles with enable state and deterministic priority;
- drag-and-drop ordering, search, filters, undo, and redo;
- dependency, incompatibility, duplicate-ID, class-shadow, and data-overlap checks;
- per-mod conflict explanations and suggested ordering;
- guarded preview, backup, atomic apply, verification, and restore;
- integrated Songs of Syx launcher settings, language selection, and system info;
- fingerprint-gated direct launch or official-launcher handoff;
- per-user self-contained installer with a desktop shortcut.

Priority `1` is the **lowest** priority. Larger numbers are higher priority. The
launcher reverses the enabled profile exactly once when writing Songs of Syx's
highest-first official `MODS` array.

## Current release

Current pre-release: `0.2.0-rc7`

Download the Windows x64 setup program or portable ZIP from
[GitHub Releases](https://github.com/Hvizeu/ChoirLauncher/releases).

The setup build is self-contained: users do not need to install .NET separately.
It installs by default to:

```text
%APPDATA%\songsofsyx\ChoirLauncher
```

and stores profiles, logs, preferences, and backups under:

```text
%LOCALAPPDATA%\ChoirLauncher
```

See [installation and upgrade instructions](docs/INSTALLATION.md).

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
modified. ChoirLauncher has no telemetry and the current release performs no
network update checks.

## Building

Requirements:

- Windows x64;
- Git LFS;
- .NET 8 SDK.

```powershell
git lfs install
git clone https://github.com/Hvizeu/ChoirLauncher.git
cd ChoirLauncher
dotnet build .\ChoirLauncher.sln -c Release
.\Tools\Run-Validation.ps1
```

Create the self-contained portable ZIP and setup executable:

```powershell
.\Tools\Build-PublicRelease.ps1 -Force
```

Generated files appear under `Release/<version>` and are intentionally ignored by
Git. Upload them as GitHub Release assets. See [building and releasing](docs/BUILDING.md).

## Contributing

Bug reports and pull requests are welcome. Do not commit generated binaries,
release archives, private Songs of Syx data, runtime logs, or installed-game files.
Run the validation command before opening a pull request and read
[CONTRIBUTING.md](CONTRIBUTING.md).

## Compatibility and limitations

- The current compatibility fingerprints support Songs of Syx `0.71.44` only.
- Future game builds require new verification before they are accepted.
- The setup executable is currently unsigned; verify release checksums before use.
- Automatic updates are not enabled yet.
- Public redistribution of the current game-derived logo/icon artwork and the
  supplied city background must be cleared before publishing binaries or this
  source tree publicly.
- The project license has not yet been selected. Add a root `LICENSE` before the
  first public release or accepting external code contributions.

Third-party notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
