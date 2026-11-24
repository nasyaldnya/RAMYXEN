# Contributing to RAMYXEN

Thank you for your interest in contributing to RAMYXEN. This repository enforces deterministic builds, local-first security boundaries, and 4-locale multilingual hygiene.

## Prerequisites
- **OS:** Windows 11 (`x64`, Build 22000+)
- **SDK:** `.NET 10 SDK 10.0.404` (pinned in [`global.json`](global.json) with `rollForward: "latestPatch"` and `allowPrerelease: false`)
- **Native Toolchain:** Visual Studio 2022 or Visual Studio 18 Build Tools with the C++ Desktop Development workload (`cl.exe`, `link.exe`, Windows 11 SDK)
- **Localization Tooling:** Node.js 20+

## Branching Conventions
- Never commit directly to `main`.
- Use descriptive topic branches: `feature/*`, `fix/*`, `chore/*`, or `docs/*`.

## Core Engineering Rules
1. **No Hardcoded User-Visible Strings:** Every visible label, tooltip, error, status, chart axis, and accessibility string must be defined in `localization/locales/en-US.json` and translated across `ar-SA.json`, `zh-CN.json`, and `ja-JP.json`.
2. **Never Commit Memory Dumps, Binaries, or Secrets:** `.dmp`, `.raw`, `.vmem`, `.etl`, `.pfx`, `.key`, `.env`, and generated build outputs under `artifacts/` are strictly prohibited.
3. **Preserve Native Isolation:** Native C++20 components (`RAMYXEN.Native.dll`, `RAMYXEN.HeapWorker.exe`) must compile cleanly with `/MT`, `/guard:cf`, and `/Brepro` without external VC++ redistributable dependencies.
4. **Verify Before Opening a Pull Request:**
   ```powershell
   .\scripts\build-release.ps1 -RunTests
   ```
   Confirm that Native C++20 tests, `.NET 10` Release tests, `tools/localization/validate.js`, and `tests/localization/run-localization-tests.js` all pass with zero errors.
5. **Record Architectural Decisions:** Significant architectural or stack decisions belong in numbered ADRs under [`docs/architecture/decisions/`](docs/architecture/decisions/).

## Licensing Notice
RAMYXEN's original source code and documentation are distributed under the **Apache License 2.0** ([LICENSE](LICENSE)). By submitting a contribution, you agree that your contribution is submitted under the terms of Apache-2.0. Third-party components remain subject to their respective licenses ([docs/security/THIRD_PARTY_NOTICES.md](docs/security/THIRD_PARTY_NOTICES.md)).
