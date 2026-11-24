# ADR-0001: Repository Structure, Design Preservation & Governance

**Status:** Accepted | **Date:** 2026-11-23

## Context
RAMYXEN transitions from Phase 1 (complete 98-screen UX/UI design and Nano Banana 2.1 visual reinvention) into Phase 2.0 engineering readiness. Existing design assets (`75` files under root and `design/`) must be preserved without churn while establishing a clean, scalable engineering layout.

## Considered Alternatives
1. **Reorganize all existing design files into `docs/design/`:** High churn, breaks existing internal `file:///` links across handoff documents and prototype relative paths (`../02_brand_identity/...`).
2. **Preserve `design/` and root handoff docs intact, add `docs/`, `localization/`, `tools/`, `tests/`, `.github/`:** Zero breakage, clear separation of design archive from engineering foundation.

## Selected Decision
Adopt **Alternative 2**. Keep `design/` intact, create dedicated `docs/engineering/`, `docs/architecture/decisions/`, `localization/`, `tools/localization/`, `tests/localization/`, and `.github/`, and defer `src/` scaffolding until Phase 2.1 stack approval.

## Consequences
- Zero broken links in existing design handoffs or the interactive prototype.
- Clear governance boundaries for future `src/` code and automated CI workflows.
