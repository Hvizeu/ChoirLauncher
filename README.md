# ChoirLauncher

[![Build](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml/badge.svg)](https://github.com/Hvizeu/ChoirLauncher/actions/workflows/build.yml)

ChoirLauncher is an independent Windows mod-profile manager and alternative
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
- dependency, incompatibility, duplicate-ID, class-shadow, and data-overlap checks;
- per-mod conflict explanations and suggested ordering;
- guarded preview, backup, atomic apply, verification, and restore;
- integrated Songs of Syx launcher settings, language selection, and system info;
- version-aware direct launch or official-launcher handoff without a fixed checksum gate;
- per-user self-contained installer with a desktop shortcut.

Priority `1` is the **lowest** priority. Larger numbers are higher priority. The
launcher reverses the enabled profile exactly once when writing Songs of Syx's
highest-first official `MODS` array.

## Current release

Current pre-release: `0.2.0-rc9`

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
```

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
- The setup executable is currently unsigned; verify release checksums before use.
- Automatic updates are not enabled yet.
- The project owner has confirmed permission to include the current artwork in
  official ChoirLauncher source and binary distributions. That permission does
  not authorize independent redistribution outside the project license.

Third-party notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
