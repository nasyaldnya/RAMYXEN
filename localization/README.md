# RAMYXEN Localization Foundation

This directory is the **single canonical source of truth** for all user-facing text in RAMYXEN.

## Directory Layout

- `languages.json` — Registered language catalogue (native names, LTR/RTL direction, fallback locale, font preferences — never country flags).
- `locales/en-US.json` — Canonical English (US) source language pack.
- `locales/ar-SA.json` — Arabic (Saudi Arabia) reference RTL language pack.
- `locales/template.json` — Blank key-complete template for adding new languages.
- `schema/locale.schema.json` — JSON Schema validating key naming and value structure.
- `glossary/technical-terms.json` — Standardized Windows memory terminology and Do-Not-Translate technical tokens.
- `glossary/translation-guidelines.md` — Rules for Arabic RTL, Bidi isolation, ICU pluralization, and memory units.
- `LOCALIZATION_ARCHITECTURE.md` — Engineering specification for runtime loading, fallback, thread safety, and RTL.
- `TRANSLATION_GUIDE.md` — Step-by-step maintainer guide for editing or adding languages without touching UI code.

## Validate Locally

```powershell
.\tools\localization\validate.ps1
node .\tests\localization\run-localization-tests.js
```
