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

ChoirLauncher uses the
[ChoirLauncher Source-Available Contribution License 1.0](LICENSE). GitHub forks
are permitted when used to prepare or submit contributions to the official
project; independent redistribution and fork releases are not permitted without
written authorization.

By submitting a contribution, you confirm that you have the right to submit it
and grant the repository owner the contribution rights stated in Section 4 of
the license. Accepted contributors will be credited in project history, release
notes, contributor records, or another reasonable project record.
