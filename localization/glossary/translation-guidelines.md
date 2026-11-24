# RAMYXEN — Translation & Technical Terminology Guidelines

1. **Preserve Technical Accuracy:** Never translate *Standby* as "consumed" or "locked"; always preserve its semantics as reclaimable/available cache (`الاحتياطية (متاحة)`). Never translate *Observed Growth* as *Confirmed Leak*.
2. **Respect Do-Not-Translate Tokens:** Keep identifiers listed in `technical-terms.json` (`RAMYXEN`, `PID`, `PFN`, `VAD`, `PTE`, `ETW`, `YARA`, `RWX`, `DLL`, `PDB`) in Latin uppercase.
3. **Preserve Placeholders & ICU Syntax:** Every `{variable}` name and ICU `plural` structure must match `en-US.json` exactly. For Arabic (`ar-SA`), always provide all 6 CLDR plural forms (`zero`, `one`, `two`, `few`, `many`, `other`).
4. **Bidirectional Safety:** Do not manually insert raw directional override characters (`RLO`/`LRO`); the application's `DirectionManager` automatically applies Unicode Bidi Isolation (`LRI`/`PDI`) around technical addresses, paths, and identifiers.
5. **Memory Units:** Preserve binary (`KiB`, `MiB`, `GiB`) vs. decimal (`KB`, `MB`, `GB`) distinctions exactly as used by the underlying Windows counter.
