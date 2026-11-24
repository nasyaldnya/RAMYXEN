# RAMYXEN — Translation & Language Editing Guide

This guide shows how to **edit an existing language** or **add a brand-new language** without modifying any application source code.

---

## Workflow 1: Editing an Existing Language (e.g., Arabic `ar-SA`)

1. Open `localization/locales/ar-SA.json` in any UTF-8 editor (VS Code, Notepad++, Visual Studio).
2. Locate the stable key you want to update (for example, `"navigation.liveMonitor"`).
3. Edit only the string value on the right side. Keep all `{placeholders}` and ICU plural syntax intact, and follow `localization/glossary/technical-terms.json`.
4. Save the file as **UTF-8 (without BOM)** with `LF` line endings.
5. Run the automated validator:
   ```powershell
   .\tools\localization\validate.ps1
   ```
6. Confirm **0 errors** and check the completeness summary.

---

## Workflow 2: Adding a New Language (e.g., French `fr-FR`)

1. Copy `localization/locales/template.json` to `localization/locales/fr-FR.json`.
2. Translate every empty value in `fr-FR.json` while preserving all keys and `{variable}` placeholders.
   - For languages with specific CLDR plural categories, include all required plural branches in ICU messages.
3. Register the new locale in `localization/languages.json`:
   ```json
   {
     "locale": "fr-FR",
     "nativeName": "Français",
     "englishName": "French (France)",
     "direction": "ltr",
     "fallbackLocale": "en-US",
     "availability": "community",
     "fontPreferences": {
       "ui": ["Segoe UI Variable", "Segoe UI", "sans-serif"],
       "mono": ["Cascadia Code", "Consolas", "monospace"]
     }
   }
   ```
   *(Never add country flags — always display languages by their `nativeName`.)*
4. Run the validator and test suite:
   ```powershell
   .\tools\localization\validate.ps1
   node .\tests\localization\run-localization-tests.js
   ```
5. Once validated with 0 errors, RAMYXEN automatically discovers and lists the new language in the Language Selector without requiring UI code changes.
