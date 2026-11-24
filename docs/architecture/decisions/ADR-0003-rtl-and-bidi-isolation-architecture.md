# ADR-0003: Arabic RTL Layout & Technical Bidi Isolation

**Status:** Accepted | **Date:** 2026-11-23

## Context
RAMYXEN ships with `ar-SA` (Arabic) as a primary reference locale alongside `en-US`. Technical memory tools mix Arabic prose with hexadecimal addresses (`0x00007FF6A0000000`), PIDs, PFNs, Windows file paths (`C:\Windows\System32\ntdll.dll`), stack frames, and chronological time-series charts.

## Considered Alternatives
1. **Naive global RTL mirroring:** Corrupts hex addresses, reverses file path backslashes, and flips chronological timelines backward.
2. **Direction-aware RTL shell + Unicode Bidi Isolation (`LRI` U+2066 / `PDI` U+2069 or explicit `dir="ltr"` / `FlowDirection="LeftToRight"`) on technical tokens + non-mirrored chronological charts:** Preserves natural Arabic reading flow while keeping technical identifiers and time-series accurate.

## Selected Decision
Adopt **Alternative 2**. Layout shells, navigation rails, menus, dialogs, tables, and directional icons adapt to `RTL`, while hex values, PIDs, PFNs, GUIDs, file paths, process names, stack traces, and chronological/radial charts remain strictly `LTR` isolated.

## Consequences
- Zero garbled hex addresses or reversed paths in Arabic UI.
- Arabic pluralization uses full 6-category CLDR ICU rules (`zero`, `one`, `two`, `few`, `many`, `other`), never binary singular/plural `if/else`.
