# Security Policy — RAMYXEN

## Handling Sensitive Memory Data
RAMYXEN analyzes Windows system and process memory, which can contain plaintext credentials, session tokens, encryption keys, and personally identifiable information (PII).

- **Local-First Architecture:** RAMYXEN operates locally with zero external telemetry by default.
- **Explicit Consent Gates:** Raw memory string extraction and full physical RAM acquisition must always require explicit user consent and appropriate elevation.
- **Repository Protection:** Never commit memory dumps (`.dmp`, `.raw`, `.vmem`, `.aff4`, `.etl`, `.rmxsnap`), private keys, or real host diagnostic captures to Git.

## External Language Pack Safety
External JSON language packs loaded at runtime are treated as **untrusted data**:
- Strict UTF-8 JSON parsing only (never evaluate/execute code or HTML scripts from locale files).
- Maximum file size limit (`5 MB` per locale file) and JSON schema validation before activation.
- Malformed or non-conforming packs are rejected safely with automatic fallback to `en-US`.

## Reporting a Vulnerability
Report suspected security issues directly to the repository maintainers via private channels. Never post memory dumps, credentials, or exploit payloads in public issue descriptions.

## .NET 10 Self-Contained Runtime Security & Servicing Policy
Because RAMYXEN ships as a self-contained `.NET 10 LTS` and `Windows App SDK` desktop distribution, previously published binaries **do not** automatically receive OS-level `.NET` security updates.

For every servicing cycle and release:
1. **Monitor Official Servicing Feeds:** Track Microsoft's `.NET 10 LTS` monthly security and servicing releases (`https://dotnet.microsoft.com/en-us/download/dotnet/10.0` and `releases.json`).
2. **Pin & Upgrade Deterministically:** Update `global.json` to the verified stable `.NET 10` servicing SDK (keeping `rollForward: "latestPatch"` and `allowPrerelease: false`, without jumping major versions unintentionally).
3. **Rebuild Both Distribution Layers:** Run `.\scripts\build-release.ps1 -RunTests` to produce fresh self-contained `RAMYXEN-Portable-win-x64.zip` and `RAMYXEN-Setup.exe` artifacts and verify the embedded runtime version in `RAMYXEN.runtimeconfig.json` and runtime binaries.
4. **Re-Verify in a Clean Environment:** Validate direct Portable launch and installer/uninstaller behavior on Windows 11 x64 without preinstalled `.NET` or `Windows App SDK` runtimes.
5. **Regenerate Checksums:** Independently recompute and publish updated `SHA256SUMS.txt` alongside every rebuilt release.
