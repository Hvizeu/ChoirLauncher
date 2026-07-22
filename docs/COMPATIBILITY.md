# Game-build compatibility

ChoirLauncher does not authorize game builds through one hardcoded checksum.

At runtime it:

1. checks an explicit override or prior validated user selection, then discovers
   Steam through Windows registry metadata, every `libraryfolders.vdf`, and app
   `1162750`'s actual `installdir`;
2. verifies that launch files are ordinary files inside that game directory;
3. checks the JAR structure and Windows executable signature;
4. reads the game version from the constant metadata in `game/VERSION.class`
   without loading game code;
5. records the actual JAR and executable SHA-256 values in diagnostics;
6. recognizes cataloged builds when possible, but does not reject an otherwise
   valid build merely because a Steam patch changed its checksum.

This distinction matters: a checksum is valuable evidence for bug reports and
build recognition, but it is not a durable compatibility rule.

If no valid game root is found automatically, setup and the desktop launcher ask
the user to select the main folder that directly contains `SongsOfSyx.jar`. The
selection is validated before it is saved. Numeric Workshop IDs read from the
official `LauncherSettings.txt` are not treated as proof that their installation
folders were scanned successfully.

The current mod-analysis behavior and integrated Songs of Syx Settings editor are
validated against v71.44. Launch can still proceed for an older or newly patched
build. If another version has an incompatible settings schema, the editor reports
that limitation and does not write the file.

The following remain blocking safety failures:

- missing game or launch files;
- files outside the discovered game root;
- reparse-point launch files;
- a malformed or structurally unrelated JAR;
- a selected Windows target without an `MZ` executable signature;
- a running game/launcher process where a guarded operation requires exclusivity;
- a held configuration lock.

When reporting a compatibility problem, include the version, JAR SHA-256,
executable SHA-256, launch route, and relevant `%LOCALAPPDATA%\ChoirLauncher\logs`
entry shown by ChoirLauncher.
