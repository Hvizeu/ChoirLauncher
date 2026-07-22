# Installing ChoirLauncher

ChoirLauncher 0.3.0-rc2 is distributed as self-contained packages. You do not
need to install .NET. Download only from the official GitHub Releases page:

https://github.com/Hvizeu/ChoirLauncher/releases

Before installing or updating, close ChoirLauncher, Songs of Syx, and the official
Songs of Syx launcher. The Linux and macOS packages are built on native GitHub
runners but have not yet received owner runtime testing on physical Linux/macOS
systems.

## Choose the correct download

| System | Download |
|---|---|
| Windows 10/11, 64-bit | `ChoirLauncher-Setup-win-x64.exe` (recommended) |
| Windows 10/11 portable, 64-bit | `ChoirLauncher-Desktop-win-x64-self-contained.zip` |
| Linux, Intel/AMD 64-bit | `ChoirLauncher-Desktop-linux-x64-self-contained.tar.gz` |
| Mac with Apple Silicon (M1/M2/M3/M4 or newer) | `ChoirLauncher-Desktop-osx-arm64-self-contained.zip` |
| Mac with Intel processor | `ChoirLauncher-Desktop-osx-x64-self-contained.zip` |

`x64` means an Intel or AMD 64-bit processor. `arm64` means Apple Silicon. Linux
ARM devices are not included in this release.

## Verify the download (recommended)

Download `SHA256SUMS.txt` from the same release. Compare the checksum of your file
with its line in that document.

Windows PowerShell:

```powershell
Get-FileHash .\ChoirLauncher-Setup-win-x64.exe -Algorithm SHA256
```

Linux:

```bash
sha256sum ChoirLauncher-Desktop-linux-x64-self-contained.tar.gz
```

macOS:

```bash
shasum -a 256 ChoirLauncher-Desktop-osx-arm64-self-contained.zip
```

For an Intel Mac, substitute the `osx-x64` filename. A mismatch means the file
must not be opened; delete it and download it again from the official release.

## Windows — setup program

1. Download `ChoirLauncher-Setup-win-x64.exe`.
2. Double-click it. The build is currently unsigned, so Windows may show a
   security prompt. Confirm the filename and checksum before continuing.
3. Setup displays two separate paths:
   - **ChoirLauncher install folder** is where the application will be installed.
   - **Songs of Syx game folder** is the existing Steam game folder.
4. If the game is not detected, choose the folder that directly contains
   `SongsOfSyx.jar`. In Steam, open Songs of Syx **Properties > Installed Files >
   Browse** to find it.
5. Finish setup and start ChoirLauncher from the desktop shortcut.

The default install folder is:

```text
%APPDATA%\songsofsyx\ChoirLauncher
```

Profiles, preferences, logs, and backups are kept separately in:

```text
%LOCALAPPDATA%\ChoirLauncher
```

To update, close ChoirLauncher and run the newer setup program. Existing profiles
and backups are preserved.

## Windows — portable ZIP

1. Download `ChoirLauncher-Desktop-win-x64-self-contained.zip`.
2. Right-click the ZIP and choose **Extract All**. Do not run the program while it
   is still inside the ZIP.
3. Open the extracted folder and run `ChoirLauncher.exe`.
4. If asked for the game location, select the main folder containing
   `SongsOfSyx.jar`.

To update a portable copy, extract the new release into a new empty folder. Your
profiles remain in `%LOCALAPPDATA%\ChoirLauncher`.

## Linux

The Linux build targets 64-bit Intel/AMD distributions using glibc and an X11
desktop session.

1. Download `ChoirLauncher-Desktop-linux-x64-self-contained.tar.gz`.
2. Open a terminal in the download directory.
3. Create a permanent user-owned folder and extract the archive:

```bash
mkdir -p "$HOME/Applications/ChoirLauncher"
tar -xzf ChoirLauncher-Desktop-linux-x64-self-contained.tar.gz \
  -C "$HOME/Applications/ChoirLauncher"
```

4. Ensure the launcher is executable and start it:

```bash
chmod +x "$HOME/Applications/ChoirLauncher/ChoirLauncher"
"$HOME/Applications/ChoirLauncher/ChoirLauncher"
```

5. If automatic Steam discovery fails, select the Songs of Syx folder containing
   `SongsOfSyx.jar`. A common Steam location is:

```text
~/.local/share/Steam/steamapps/common/Songs of Syx
```

Flatpak and Snap Steam roots are also checked automatically.

If the window does not open because a native desktop library is missing, install
the standard Avalonia X11 dependencies. Debian/Ubuntu example:

```bash
sudo apt update
sudo apt install libx11-6 libice6 libsm6 libfontconfig1
```

Use your distribution's equivalent packages on Fedora, Arch, or another Linux
distribution. The .NET runtime itself is already included.

Profiles and backups are stored in `$XDG_DATA_HOME/ChoirLauncher` when
`XDG_DATA_HOME` is set, otherwise in `~/.local/share/ChoirLauncher`.

To update, close ChoirLauncher, remove the old application files from
`~/Applications/ChoirLauncher`, and extract the new archive there. Do not delete
the separate data folder unless you also want to erase profiles and backups.

## macOS

First check the processor type through **Apple menu > About This Mac**:

- Apple M-series processor: download `osx-arm64`.
- Intel processor: download `osx-x64`.

Then:

1. Download the matching ZIP.
2. Double-click the ZIP to extract `ChoirLauncher.app`.
3. Drag `ChoirLauncher.app` into the **Applications** folder.
4. Because this release is not Apple-notarized, the first normal double-click may
   be blocked by Gatekeeper. Control-click `ChoirLauncher.app`, choose **Open**,
   then choose **Open** again. If macOS still blocks it, open **System Settings >
   Privacy & Security** and use **Open Anyway** for ChoirLauncher. Do not disable
   Gatekeeper globally.
5. If asked for the game location, select `SongsOfSyxMac.app` or the Steam folder
   containing it. ChoirLauncher resolves the internal
   `Contents/Resources/SongsOfSyx.jar` automatically.

A common Steam location is:

```text
~/Library/Application Support/Steam/steamapps/common/Songs of Syx
```

Profiles and backups are stored in:

```text
~/Library/Application Support/ChoirLauncher
```

To update, quit ChoirLauncher and replace the old `ChoirLauncher.app` in
Applications with the newer one. The separate profiles and backups remain intact.

## Troubleshooting

- **The launcher asks for the game folder:** select the main Songs of Syx folder,
  not the ChoirLauncher folder. The selection must resolve to `SongsOfSyx.jar`.
- **Workshop mods show as missing:** confirm Steam and Songs of Syx are installed
  for the same user and select the actual game folder when prompted.
- **The application was moved:** delete only the saved `game-location.json` from
  ChoirLauncher's platform data folder, restart, and select the game again.
- **A security warning appears:** verify the SHA-256 and use only the operating
  system's per-application approval. Never disable system-wide protection.
- **A disabled development copy is named in a JVM-agent warning:** update to
  0.3.0-rc2. Direct Linux/macOS launch ignores persistent `JVM_ARGS2` agents and
  injects only an approved agent owned by an enabled mod. The native official
  launcher still blocks genuinely stale or disabled persistent entries.
- **Still stuck:** open a GitHub issue and include the operating system,
  architecture, ChoirLauncher version, the exact error, and the manager log. Do
  not upload private profiles, saves, or `LauncherSettings.txt` unless you have
  reviewed them first.

ChoirLauncher never needs to be installed inside the Songs of Syx game directory
and never replaces game executables or `SongsOfSyx.jar`.
