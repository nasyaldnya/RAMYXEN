/**
 * RAMYXEN Automated Localization Infrastructure Test Suite
 * Tests: Validator on live locales, Fallback Resolution, RTL Bidi Isolation,
 * Arabic 6-form CLDR Pluralization, Untrusted Pack Safety, and Controlled Add/Edit Language Workflow.
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { validateLocalization } = require('../../tools/localization/validate.js');

const REPO_ROOT = path.resolve(__dirname, '../..');
let passed = 0;

function test(name, fn) {
  try {
    fn();
    console.log(`  [PASS] ${name}`);
    passed++;
  } catch (err) {
    console.error(`  [FAIL] ${name}: ${err.message}`);
    process.exitCode = 1;
  }
}

// Reference Fallback Resolver (mirrors LocalizationService architecture)
function resolveKey(key, selectedPack, parentPack, canonicalPack) {
  if (selectedPack && typeof selectedPack[key] === 'string' && selectedPack[key].length > 0) return selectedPack[key];
  if (parentPack && typeof parentPack[key] === 'string' && parentPack[key].length > 0) return parentPack[key];
  if (canonicalPack && typeof canonicalPack[key] === 'string' && canonicalPack[key].length > 0) return canonicalPack[key];
  return `[missing:${key}]`;
}

// Reference Unicode Bidi Isolator for technical values inside RTL UI (LRI + LRM + token + LRM + PDI)
function isolateLtr(token) {
  return `\u2066\u200E${token}\u200E\u2069`;
}

// Reference Arabic CLDR 6-category plural selector
function arabicPluralCategory(n) {
  if (n === 0) return 'zero';
  if (n === 1) return 'one';
  if (n === 2) return 'two';
  const mod100 = n % 100;
  if (mod100 >= 3 && mod100 <= 10) return 'few';
  if (mod100 >= 11 && mod100 <= 99) return 'many';
  return 'other';
}

console.log('\n=== RAMYXEN Localization Infrastructure Tests ===');

test('1. Repository locales (en-US, ar-SA, zh-CN, ja-JP, template.json) pass full validation with 0 errors', () => {
  const res = validateLocalization(REPO_ROOT);
  assert.strictEqual(res.ok, true, `Validation errors: ${res.errors.join('; ')}`);
  assert.strictEqual(res.report.length, 4);
  for (const r of res.report) {
    assert.strictEqual(r.completeness, '100.0%', `${r.locale} completeness was ${r.completeness}`);
  }
});

test('2. RTL metadata & Arabic native name (no flags) verified in languages.json', () => {
  const cat = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'localization/languages.json'), 'utf8'));
  const ar = cat.languages.find(l => l.locale === 'ar-SA');
  assert.ok(ar, 'ar-SA missing from catalogue');
  assert.strictEqual(ar.direction, 'rtl');
  assert.strictEqual(ar.nativeName, 'العربية');
  assert.strictEqual(ar.fallbackLocale, 'en-US');
});

test('3. Fallback resolution hierarchy (selected -> parent -> en-US -> safe marker)', () => {
  const en = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'localization/locales/en-US.json'), 'utf8'));
  const arSA = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'localization/locales/ar-SA.json'), 'utf8'));
  const arEG = { 'app.tagline': 'افهم كل بايت (مصر).' }; // partial child override
  assert.strictEqual(resolveKey('app.tagline', arEG, arSA, en), 'افهم كل بايت (مصر).');
  assert.strictEqual(resolveKey('navigation.overview', arEG, arSA, en), arSA['navigation.overview']);
  assert.strictEqual(resolveKey('navigation.overview', {}, {}, en), 'Overview');
  assert.strictEqual(resolveKey('nonexistent.key', {}, {}, en), '[missing:nonexistent.key]');
});

test('4. Unicode Bidi Isolation preserves LTR hex addresses and file paths inside RTL strings', () => {
  const isolatedAddr = isolateLtr('0x00007FF6A0000000');
  const isolatedPath = isolateLtr('C:\\Windows\\System32\\ntdll.dll');
  assert.ok(isolatedAddr.startsWith('\u2066') && isolatedAddr.endsWith('\u2069'));
  assert.ok(isolatedPath.includes('C:\\Windows\\System32\\ntdll.dll'));
});

test('5. Arabic CLDR 6-category pluralization covers zero, one, two, few, many, other', () => {
  const ar = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'localization/locales/ar-SA.json'), 'utf8'));
  assert.strictEqual(arabicPluralCategory(0), 'zero');
  assert.strictEqual(arabicPluralCategory(1), 'one');
  assert.strictEqual(arabicPluralCategory(2), 'two');
  assert.strictEqual(arabicPluralCategory(5), 'few');
  assert.strictEqual(arabicPluralCategory(15), 'many');
  assert.strictEqual(arabicPluralCategory(100), 'other');
  for (const cat of ['zero', 'one', 'two', 'few', 'many', 'other']) {
    assert.ok(ar['process.count'].includes(`${cat} {`), `ar-SA process.count missing ${cat}`);
  }
});

test('6. Controlled Add-Language & Edit-Language workflow (fr-FR) works with ZERO UI code changes', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ramyxen-i18n-demo-'));
  try {
    fs.cpSync(path.join(REPO_ROOT, 'localization'), path.join(tmp, 'localization'), { recursive: true });
    // Step A: Copy template.json -> fr-FR.json and populate from en-US as simulated French pack
    const en = JSON.parse(fs.readFileSync(path.join(tmp, 'localization/locales/en-US.json'), 'utf8'));
    const fr = { ...en, 'app.tagline': 'Comprenez chaque octet.', 'navigation.overview': 'Vue d’ensemble' };
    fs.writeFileSync(path.join(tmp, 'localization/locales/fr-FR.json'), JSON.stringify(fr, null, 2) + '\n', 'utf8');
    // Step B: Register fr-FR in languages.json
    const catPath = path.join(tmp, 'localization/languages.json');
    const cat = JSON.parse(fs.readFileSync(catPath, 'utf8'));
    cat.languages.push({ locale: 'fr-FR', nativeName: 'Français', englishName: 'French (France)', direction: 'ltr', fallbackLocale: 'en-US', availability: 'community', pluralCategories: ['one', 'other'] });
    fs.writeFileSync(catPath, JSON.stringify(cat, null, 2) + '\n', 'utf8');
    // Step C: Validate — must pass with 5 locales!
    const okRes = validateLocalization(tmp);
    assert.strictEqual(okRes.ok, true, `Expected fr-FR to pass: ${okRes.errors.join('; ')}`);
    assert.strictEqual(okRes.report.length, 5);
    // Step D: Introduce deliberate placeholder error & missing key -> validator MUST catch both!
    delete fr['common.cancel'];
    fr['charts.axis.memory'] = 'Mémoire ({wrongVar})';
    fs.writeFileSync(path.join(tmp, 'localization/locales/fr-FR.json'), JSON.stringify(fr, null, 2) + '\n', 'utf8');
    const failRes = validateLocalization(tmp);
    assert.strictEqual(failRes.ok, false);
    assert.ok(failRes.errors.some(e => e.includes('missing key "common.cancel"')));
    assert.ok(failRes.errors.some(e => e.includes('placeholder mismatch')));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('7. Untrusted Pack Safety rejects malformed JSON and raw Bidi override characters', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ramyxen-i18n-safety-'));
  try {
    fs.cpSync(path.join(REPO_ROOT, 'localization'), path.join(tmp, 'localization'), { recursive: true });
    fs.writeFileSync(path.join(tmp, 'localization/locales/ar-SA.json'), '{ "app.name": "RAMYXEN", \u202E INVALID', 'utf8');
    const res = validateLocalization(tmp);
    assert.strictEqual(res.ok, false);
    assert.ok(res.errors.some(e => e.includes('Bidi override')));
    assert.ok(res.errors.some(e => e.includes('invalid JSON')));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

console.log(`\nSummary: ${passed} / 7 localization checks passed.\n`);
