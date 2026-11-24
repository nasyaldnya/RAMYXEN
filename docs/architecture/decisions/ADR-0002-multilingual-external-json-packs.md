# ADR-0002: External JSON Language Packs & Single Source of Truth

**Status:** Accepted | **Date:** 2026-11-23

## Context
RAMYXEN must be multilingual from day one (`en-US` LTR and `ar-SA` RTL initially, scalable to any locale). Maintainers must be able to edit translations or add a new language without modifying UI code or recompiling the application.

## Considered Alternatives
1. **Windows `.resw` / PRI compiled resources only:** Requires recompilation to add or edit languages; painful for external contributors; cannot load runtime external packs cleanly.
2. **Dual `.resw` (XAML) + JSON (business logic):** Creates two competing sources of truth and drift.
3. **Single canonical external UTF-8 JSON language packs (`localization/locales/<locale>.json`) + ICU MessageFormat + centralized `LocalizationService`:** One canonical source, human-editable, runtime-reloadable, stack-agnostic (works cleanly with WinUI 3 via a custom XAML markup extension / observable binding OR with Tauri/WebView2).

## Selected Decision
Adopt **Alternative 3**. All user-visible strings live in UTF-8 JSON packs validated against `localization/schema/locale.schema.json`. Bundled packs ship inside the app directory as safe fallbacks; an external user override directory (`%LOCALAPPDATA%\RAMYXEN\localization\locales\`) allows runtime edits/additions without recompilation (even under MSIX read-only install directories).

## Consequences
- Adding or editing a language requires zero UI code changes.
- Requires a thin XAML binding/markup helper if WinUI 3 is selected in Phase 2.1 so runtime language switches update active views on the UI dispatcher thread without restarting.
