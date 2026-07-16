# Building and releasing

## Requirements

- Windows x64;
- Git LFS 3.x;
- .NET 8 SDK;
- PowerShell 5.1 or later.

Restore LFS assets after cloning:

```powershell
git lfs install
git lfs pull
```

Build and validate:

```powershell
dotnet build .\ChoirLauncher.sln -c Debug
.\Tools\Run-Validation.ps1
```

Create release artifacts:

```powershell
.\Tools\Build-PublicRelease.ps1 -Force
```

The release tool builds and tests Debug and Release, publishes the Windows x64
self-contained desktop application, embeds the verified ZIP into the self-contained
setup executable, performs clean-install and in-place-upgrade smoke tests in a
temporary staging directory, and writes a release manifest plus `SHA256SUMS.txt`.

`Release/` is ignored by Git. Upload the setup executable, portable ZIP, manifest,
checksums, project license, and third-party notices to a GitHub Release instead of
committing them.

The project owner has selected the ChoirLauncher Source-Available Contribution
License 1.0 and confirmed permission to include the current artwork in official
source and binary distributions. Windows code signing remains a documented future
release decision.
