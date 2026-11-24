#!/usr/bin/env node
/**
 * RAMYXEN Localization Validator (Zero-Dependency)
 * Validates languages.json, locale JSON files, UTF-8/Unicode safety, duplicate keys,
 * schema compliance, placeholder parity, ICU pluralization, and glossary rules.
 */
const fs = require('fs');
const path = require('path');

const MAX_PACK_BYTES = 5 * 1024 * 1024; // 5 MB untrusted pack safety limit
const KEY_REGEX = /^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$/;
const LOCALE_REGEX = /^[a-z]{2,3}-[A-Z]{2}$/;
const FLAG_EMOJI_REGEX = /[\u{1F1E6}-\u{1F1FF}]/u;
const BIDI_OVERRIDE_REGEX = /[\u202A-\u202E]/;

function findDuplicateKeys(raw) {
  const dups = [];
  const seen = new Set();
  const re = /^\s*"([^"]+)"\s*:/gm;
  let m;
  while ((m = re.exec(raw)) !== null) {
    if (seen.has(m[1])) dups.push(m[1]);
    seen.add(m[1]);
  }
  return dups;
}

function extractPlaceholders(str) {
  const vars = new Set();
  const re = /\{([a-zA-Z0-9_]+)(?:\s*,|\s*\})/g;
  let m;
  while ((m = re.exec(str)) !== null) vars.add(m[1]);
  return [...vars].sort();
}

function checkBalancedBraces(str) {
  let depth = 0;
  for (const ch of str) {
    if (ch === '{') depth++;
    else if (ch === '}') { depth--; if (depth < 0) return false; }
  }
  return depth === 0;
}

function validateLocalization(rootDir) {
  const locDir = path.join(rootDir, 'localization');
  const errors = [];
  const warnings = [];
  const report = [];

  const catPath = path.join(locDir, 'languages.json');
  if (!fs.existsSync(catPath)) {
    return { ok: false, errors: ['Missing localization/languages.json'], warnings, report };
  }

  let catalogue;
  try {
    catalogue = JSON.parse(fs.readFileSync(catPath, 'utf8'));
  } catch (e) {
    return { ok: false, errors: [`Invalid JSON in languages.json: ${e.message}`], warnings, report };
  }

  const canonicalLocale = catalogue.canonicalLocale || 'en-US';
  const canonicalPath = path.join(locDir, 'locales', `${canonicalLocale}.json`);
  if (!fs.existsSync(canonicalPath)) {
    errors.push(`Canonical locale file missing: ${canonicalPath}`);
    return { ok: false, errors, warnings, report };
  }

  const canonicalRaw = fs.readFileSync(canonicalPath, 'utf8');
  const canonicalData = JSON.parse(canonicalRaw);
  const canonicalKeys = Object.keys(canonicalData);

  // Load glossary do-not-translate tokens and verify brand identity rule
  const glossaryPath = path.join(locDir, 'glossary', 'technical-terms.json');
  const glossary = fs.existsSync(glossaryPath) ? JSON.parse(fs.readFileSync(glossaryPath, 'utf8')) : { doNotTranslate: [] };
  const protectedBrandTokens = Array.isArray(glossary.doNotTranslate) ? glossary.doNotTranslate : [];

  // Validate template.json parity
  const tplPath = path.join(locDir, 'locales', 'template.json');
  if (!fs.existsSync(tplPath)) {
    errors.push('Missing localization/locales/template.json');
  } else {
    const tpl = JSON.parse(fs.readFileSync(tplPath, 'utf8'));
    const tplKeys = Object.keys(tpl);
    for (const k of canonicalKeys) {
      if (!(k in tpl)) errors.push(`template.json: missing key "${k}"`);
    }
    for (const k of tplKeys) {
      if (!(k in canonicalData)) errors.push(`template.json: unknown extra key "${k}"`);
    }
  }

  // Validate each registered language in languages.json
  for (const lang of (catalogue.languages || [])) {
    if (!LOCALE_REGEX.test(lang.locale || '')) {
      errors.push(`languages.json: invalid BCP-47 locale "${lang.locale}"`);
    }
    if (!['ltr', 'rtl'].includes(lang.direction)) {
      errors.push(`languages.json (${lang.locale}): direction must be "ltr" or "rtl"`);
    }
    if (FLAG_EMOJI_REGEX.test(lang.nativeName || '')) {
      errors.push(`languages.json (${lang.locale}): country flag emojis are prohibited in nativeName`);
    }

    const filePath = path.join(locDir, 'locales', `${lang.locale}.json`);
    if (!fs.existsSync(filePath)) {
      errors.push(`${lang.locale}: locale file missing at localization/locales/${lang.locale}.json`);
      continue;
    }

    const stat = fs.statSync(filePath);
    if (stat.size > MAX_PACK_BYTES) {
      errors.push(`${lang.locale}.json: exceeds safety cap of 5 MB (${stat.size} bytes)`);
      continue;
    }

    const buf = fs.readFileSync(filePath);
    if (buf[0] === 0xEF && buf[1] === 0xBB && buf[2] === 0xBF) {
      errors.push(`${lang.locale}.json: UTF-8 BOM detected; save as UTF-8 without BOM`);
    }

    const raw = buf.toString('utf8');
    if (raw.includes('\uFFFD')) {
      errors.push(`${lang.locale}.json: invalid Unicode replacement character (U+FFFD) found`);
    }
    if (BIDI_OVERRIDE_REGEX.test(raw)) {
      errors.push(`${lang.locale}.json: raw Bidi override control character (U+202A–U+202E) prohibited`);
    }

    const dups = findDuplicateKeys(raw);
    for (const d of dups) errors.push(`${lang.locale}.json: duplicate key "${d}"`);

    let data;
    try {
      data = JSON.parse(raw);
    } catch (e) {
      errors.push(`${lang.locale}.json: invalid JSON — ${e.message}`);
      continue;
    }

    let translatedCount = 0;
    for (const k of canonicalKeys) {
      if (!(k in data)) {
        errors.push(`${lang.locale}.json: missing key "${k}"`);
        continue;
      }
      const val = data[k];
      if (typeof val !== 'string' || val.trim().length === 0) {
        errors.push(`${lang.locale}.json [${k}]: empty or non-string value`);
        continue;
      }
      translatedCount++;
      if (!checkBalancedBraces(val)) {
        errors.push(`${lang.locale}.json [${k}]: unbalanced ICU braces`);
      }

      // Placeholder parity
      const expectedVars = extractPlaceholders(canonicalData[k]);
      const actualVars = extractPlaceholders(val);
      if (JSON.stringify(expectedVars) !== JSON.stringify(actualVars)) {
        errors.push(`${lang.locale}.json [${k}]: placeholder mismatch (expected [${expectedVars}], got [${actualVars}])`);
      }

      // ICU plural category check
      if (canonicalData[k].includes(', plural,')) {
        if (!val.includes(', plural,')) {
          errors.push(`${lang.locale}.json [${k}]: missing ICU plural structure`);
        } else if (Array.isArray(lang.pluralCategories)) {
          for (const cat of lang.pluralCategories) {
            if (!new RegExp(`\\b${cat}\\s*\\{`).test(val)) {
              errors.push(`${lang.locale}.json [${k}]: missing required CLDR plural category "${cat}" for ${lang.locale}`);
            }
          }
        }
      }

      // Critical Do-Not-Translate check for brand name and protected glossary tokens (F-052)
      if (k === 'app.name' && protectedBrandTokens.includes('RAMYXEN') && val !== 'RAMYXEN') {
        errors.push(`${lang.locale}.json [app.name]: brand name must remain "RAMYXEN"`);
      }
      if (lang.locale !== canonicalLocale && protectedBrandTokens.includes('RAMYXEN')) {
        if (canonicalData[k].includes('RAMYXEN') && !val.includes('RAMYXEN')) {
          errors.push(`${lang.locale}.json [${k}]: protected glossary brand token "RAMYXEN" must not be translated or omitted`);
        }
      }
    }

    for (const k of Object.keys(data)) {
      if (!KEY_REGEX.test(k)) {
        errors.push(`${lang.locale}.json [${k}]: key does not match schema pattern`);
      }
      if (!(k in canonicalData)) {
        errors.push(`${lang.locale}.json: unknown extra key "${k}" not in ${canonicalLocale}.json`);
      }
    }

    const pct = canonicalKeys.length ? ((translatedCount / canonicalKeys.length) * 100).toFixed(1) : '0.0';
    report.push({
      locale: lang.locale,
      nativeName: lang.nativeName,
      direction: lang.direction.toUpperCase(),
      translated: translatedCount,
      total: canonicalKeys.length,
      completeness: `${pct}%`
    });
  }

  return { ok: errors.length === 0, errors, warnings, report };
}

if (require.main === module) {
  const rootDir = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(__dirname, '../..');
  const res = validateLocalization(rootDir);
  console.log('\n=== RAMYXEN Localization Validation Report ===');
  for (const r of res.report) {
    console.log(`  ${r.locale.padEnd(8)} | ${r.direction.padEnd(3)} | ${String(r.translated).padStart(3)}/${r.total} keys | ${r.completeness.padStart(6)} | ${r.nativeName}`);
  }
  if (res.errors.length > 0) {
    console.error(`\nFAILED with ${res.errors.length} error(s):`);
    for (const e of res.errors) console.error(`  [ERROR] ${e}`);
    process.exit(1);
  }
  console.log(`\nPASS: All ${res.report.length} registered locale(s) and template.json are valid (0 errors).\n`);
}

module.exports = { validateLocalization };
