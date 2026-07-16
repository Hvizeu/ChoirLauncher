# Installation and upgrades

## Installer

1. Download `ChoirLauncher-Setup-win-x64.exe` and `SHA256SUMS.txt` from the same
   GitHub Release.
2. Verify the published SHA-256 when possible.
3. Exit ChoirLauncher and Songs of Syx.
4. Run setup and confirm the per-user destination:

```text
%APPDATA%\songsofsyx\ChoirLauncher
```

5. Launch from the desktop shortcut.

Setup verifies its embedded portable payload before extraction, uses a sibling
staging directory, replaces an existing installation atomically, and restores the
previous installation when promotion fails.

Profiles and backups are stored separately under `%LOCALAPPDATA%\ChoirLauncher`, so
an application upgrade does not replace them.

## Portable ZIP

Extract the self-contained ZIP into a user-owned directory and run
`ChoirLauncher.exe`. Do not run it inside the ZIP.

## Updating

Download and run the newer setup while ChoirLauncher is closed. The current release
does not perform automatic network updates.

## Uninstalling

The RC7 installer does not yet register a Control Panel uninstaller. Delete:

```text
%APPDATA%\songsofsyx\ChoirLauncher
%USERPROFILE%\Desktop\ChoirLauncher.lnk
```

Delete `%LOCALAPPDATA%\ChoirLauncher` only when you also want to remove profiles,
logs, backups, and preferences.

ChoirLauncher does not install or modify Songs of Syx game binaries.
