# ADR-0004: License Deferral, Dump Protection & Untrusted Pack Safety

**Status:** Accepted | **Date:** 2026-11-23

## Context
RAMYXEN is an independent Windows 11 memory intelligence project handling highly sensitive system and process memory data.

## Selected Decision
1. **License Deferral:** No `LICENSE` file is created in Phase 2.0 until licensing research (MIT vs. Apache-2.0 vs. GPLv3) is formally approved by the project owner.
2. **Git Dump Protection:** `.gitignore` explicitly blocks all memory dump formats (`.dmp`, `.raw`, `.vmem`, `.aff4`, `.etl`, `.rmxsnap`) and credentials/certificates.
3. **Untrusted Language Packs:** External JSON locale packs are strictly parsed as data only (schema-validated, size-capped at 5 MB, no executable expressions or raw HTML injection).

## Consequences
- Eliminates accidental legal commitment to an unapproved license.
- Prevents accidental commits of confidential RAM dumps or malicious locale pack payloads.
