# Changelog

All notable public changes will be recorded here.

## 0.2.0-rc12 - 2026-07-21

- Adds hash-bound Java-agent launch support for enabled mods that require
  pre-start JVM instrumentation.
- Adds a curated SoSTransit recipe so Workshop item `3753609143` can launch
  through ChoirLauncher without manual `LauncherSettings.txt` edits.
- Adds safe Songs of Syx texture-cache invalidation for agent-backed mods that
  need the game atlas rebuilt before startup.
- Keeps Java-agent injection transient to the child game process and leaves
  `LauncherSettings.txt` unchanged unless the selected launch action explicitly
  applies a profile.

## 0.2.0-rc10 - 2026-07-20

- Keeps long Play/apply compatibility-warning text inside a vertical scroll area.
- Keeps the acknowledgement button visible when a compatibility warning contains
  many details.
- Retains the RC9 profile-import workflow and version-aware launch behavior.

## 0.2.0-rc9 - 2026-07-17

- Expanded **Import** into a guided menu for importing either a ChoirLauncher
  profile JSON file or a saved Songs of Syx `LauncherSettings.txt` mod list.
- Lets the user create a new profile from an import or replace the current
  profile after confirmation.
- Preserves the official game's highest-first `MODS` order while presenting the
  imported profile in ChoirLauncher's low-to-high priority order.
- Keeps missing imported mods as disabled unresolved entries, so the profile can
  be repaired rather than silently discarding its list.

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
