## Summary
<!-- Describe what changed and why. -->

## Type of Change
- [ ] Feature (`feature/*`)
- [ ] Bug fix (`fix/*`)
- [ ] Maintenance / Infrastructure (`chore/*`)
- [ ] Localization / Translation update
- [ ] Documentation / ADR (`docs/*`)

## Mandatory Verification Checklist
- [ ] No memory dumps (`.dmp`, `.raw`, `.vmem`, `.etl`, `.rmxsnap`), secrets, or credentials included.
- [ ] No hardcoded user-visible strings added; all new strings added to `localization/locales/en-US.json`, `ar-SA.json`, and `template.json`.
- [ ] `.\tools\localization\validate.ps1` passes with 0 errors.
- [ ] `node .\tests\localization\run-localization-tests.js` passes all tests.
- [ ] Arabic RTL layout and technical LTR Bidi isolation considered where UI is affected.
