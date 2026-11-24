# RAMYXEN — Technical Decisions Register

This document summarizes the architectural decisions recorded in [docs/architecture/decisions/](decisions/README.md).

---

## Recorded Architecture Decision Records (ADRs)

| ADR | Decision | Status |
|---|---|---|
| [ADR-0001](decisions/ADR-0001-repository-structure-and-governance.md) | Establish clean repository structure and governance across `src/`, `tests/`, `localization/`, `assets/`, `docs/`, `scripts/`, `installers/`, `tools/`, and `.github/`. | Accepted |
| [ADR-0002](decisions/ADR-0002-multilingual-external-json-packs.md) | Use external UTF-8 JSON language packs (`localization/locales/*.json`) with ICU MessageFormat syntax and a centralized Localization Service as the single source of truth (rejecting dual `.resw` + JSON splits). | Accepted |
| [ADR-0003](decisions/ADR-0003-rtl-and-bidi-isolation-architecture.md) | First-class RTL `FlowDirection` + Unicode Bidirectional Isolation (`LRI`/`PDI`) for technical identifiers (hex addresses, PIDs, paths, stack traces) and non-mirrored chronological charts. | Accepted |
| [ADR-0004](decisions/ADR-0004-licensing-deferral-and-privacy-boundaries.md) | Defer `LICENSE` file creation until formal owner licensing authorization; enforce `.gitignore` hard-blocks against RAM dumps/ETW traces and untrusted-data handling for external language packs. | Accepted |
