# Changelog

All notable public changes will be recorded here.

## 0.2.0-rc8 - 2026-07-16

- Removed the brittle global checksum authorization for `SongsOfSyx.jar`,
  `SyxWithout.exe`, and `SongsofSyx.exe`.
- Added safe game-version detection from `game/VERSION.class` without loading game
  code.
- Kept JAR and executable SHA-256 values as nonblocking known-build diagnostics.
- Added structural game-root, JAR, reparse-point, and Windows executable checks.
- Made mod compatibility analysis use the detected game version when available.
- Decoupled launch and the Info screen from the v71.44 launcher-settings schema.
- Verified compatibility behavior against an older game-version artifact with
  previously unknown hashes.

The integrated settings editor remains validated against v71.44 and fails without
writing if another version lacks its required settings fields.

## 0.2.0-rc7 - 2026-07-16

- Added a self-contained Windows x64 installer and desktop shortcut.
- Added verified direct-game and official-launcher handoff.
- Added integrated game settings, language selection, and system information.
- Added persistent profiles, deterministic low-to-high priority editing, conflict
  explanations, suggested order, backups, and rollback.
- Added compatibility warnings and missing-mod save-risk diagnostics.
- Added a permanent Default profile and profile inventory reconciliation.
- Published under the ChoirLauncher Source-Available Contribution License 1.0.

This remains a pre-release limited to Songs of Syx 0.71.44.
