# Contributing

Thank you for helping improve ChoirLauncher.

## Before opening a pull request

1. Create a focused branch from `main`.
2. Keep UI coordination in the Desktop project and reusable behavior in Core.
3. Treat mod files, archives, profiles, and launcher settings as untrusted input.
4. Do not load mod classes or invoke mod entrypoints during discovery.
5. Do not commit `bin`, `obj`, `Release`, PDBs, installers, ZIPs, runtime logs,
   profiles, saves, installed-game files, or private absolute paths.
6. Store large source artwork through Git LFS.
7. Add or update tests for behavior changes.
8. Run:

```powershell
.\Tools\Run-Validation.ps1
```

Describe the problem, the selected solution, security implications, and the tests
performed in the pull request.

## Binary and dependency changes

Do not submit compiled third-party binaries. Explain every new package, version,
license, and runtime reason. Maintainers will review changes in the private
authoritative development environment before publishing an official artifact.

## Licensing

The repository owner must select a project license before external contributions
can be accepted. Until a root `LICENSE` exists, treat pull requests as proposals
that require an explicit licensing decision.
