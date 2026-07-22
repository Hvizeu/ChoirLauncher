# Installation and upgrades

## Installer

1. Download `ChoirLauncher-Setup-win-x64.exe` and `SHA256SUMS.txt` from the same
   GitHub Release.
2. Verify the published SHA-256 when possible.
3. Exit ChoirLauncher and Songs of Syx.
4. Run setup. Confirm the two paths shown before installation:

   - **ChoirLauncher install folder** — the per-user destination below;
   - **Songs of Syx game folder** — the detected folder that directly contains
     `SongsOfSyx.jar`.

```text
%APPDATA%\songsofsyx\ChoirLauncher
```

5. If setup cannot discover Songs of Syx automatically, select **OK** when asked
   to locate it, then choose the main Songs of Syx installation folder. Setup
   rejects unrelated folders and offers Retry or Cancel.
6. Launch from the desktop shortcut.

Setup verifies its embedded portable payload before extraction, uses a sibling
staging directory, replaces an existing installation atomically, and restores the
previous installation when promotion fails.

Setup and `ChoirLauncher.exe` include explicit Windows 10/11 compatibility
manifests and the same native application icon. After creating or updating the
desktop shortcut, setup notifies Windows Shell so Explorer refreshes any cached
generic icon. A successful install should not produce a Program Compatibility
Assistant warning.

Profiles and backups are stored separately under `%LOCALAPPDATA%\ChoirLauncher`, so
an application upgrade does not replace them.

The validated game folder is stored separately at:

```text
%LOCALAPPDATA%\ChoirLauncher\game-location.json
```

If that folder is moved or a portable build has no saved location, ChoirLauncher
opens the same clear folder-selection flow before scanning mods.

## Portable ZIP

Extract the self-contained ZIP into a user-owned directory and run
`ChoirLauncher.exe`. Do not run it inside the ZIP. If automatic Steam discovery
fails, select the main game folder containing `SongsOfSyx.jar` when prompted.

## Updating

Download and run the newer setup while ChoirLauncher is closed. The current release
does not perform automatic network updates.

## Uninstalling

 The installer does not yet register a Control Panel uninstaller. Delete:

```text
%APPDATA%\songsofsyx\ChoirLauncher
%USERPROFILE%\Desktop\ChoirLauncher.lnk
```

Delete `%LOCALAPPDATA%\ChoirLauncher` only when you also want to remove profiles,
logs, backups, and preferences.

ChoirLauncher does not install or modify Songs of Syx game binaries.
