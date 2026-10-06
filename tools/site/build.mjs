// SITE-010: builds the product website — one page per language (good for search engines: /he/, /de/, …, with hreflang),
// plus a start page that sends each visitor to the language of their region (I18N-020).
// English is the source: {{English text}} in site/template.html, translations in site/i18n/<lang>.json, {{=name}} values
// from site/site.json. A text without a translation fails the build (no half-translated pages go out).
//   node tools/site/build.mjs [outDir=site/dist]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const site = path.join(root, 'site');
const out = path.resolve(process.argv[2] || path.join(site, 'dist'));
const cfg = JSON.parse(fs.readFileSync(path.join(site, 'site.json'), 'utf8'));
const tpl = fs.readFileSync(path.join(site, 'template.html'), 'utf8');
const LANGS = { en: 'English', he: 'עברית', ar: 'العربية', de: 'Deutsch', es: 'Español', pt: 'Português', fr: 'Français', it: 'Italiano', nl: 'Nederlands', pl: 'Polski', tr: 'Türkçe', ja: '日本語', zh: '中文' };
const RTL = ['he', 'ar'];
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
const keys = [...new Set([...tpl.matchAll(/\{\{(?!=)([\s\S]+?)\}\}/g)].map((m) => m[1]))];

fs.rmSync(out, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
const missing = [];
for (const [lang] of Object.entries(LANGS)) {
  const dict = lang === 'en' ? {} : JSON.parse(fs.readFileSync(path.join(site, 'i18n', lang + '.json'), 'utf8'));
  for (const k of keys) if (lang !== 'en' && !dict[k]) missing.push(lang + ': ' + k);
  const vals = {
    lang, dir: RTL.includes(lang) ? 'rtl' : 'ltr', product: esc(cfg.product), portal: esc(cfg.portalUrl), email: esc(cfg.email), base: esc(cfg.baseUrl), year: String(new Date().getFullYear()),
    hreflang: Object.keys(LANGS).map((l) => `<link rel="alternate" hreflang="${l}" href="${esc(cfg.baseUrl)}/${l}/">`).join('\n') + `\n<link rel="alternate" hreflang="x-default" href="${esc(cfg.baseUrl)}/">`,
    langOptions: Object.entries(LANGS).map(([l, n]) => `<option value="${l}"${l === lang ? ' selected' : ''}>${n}</option>`).join(''),
    langLinks: Object.entries(LANGS).map(([l, n]) => `<a href="../${l}/" hreflang="${l}" lang="${l}">${n}</a>`).join(''),
  };
  const html = tpl.replace(/\{\{=(\w+)\}\}/g, (m, k) => { if (!(k in vals)) throw new Error('unknown value ' + k); return vals[k]; })
    .replace(/\{\{([\s\S]+?)\}\}/g, (m, k) => esc(dict[k] || k));
  fs.mkdirSync(path.join(out, lang), { recursive: true });
  fs.writeFileSync(path.join(out, lang, 'index.html'), html);
}
if (missing.length) { console.error('Missing translations:\n' + missing.join('\n')); process.exit(1); }
for (const f of ['site.css', 'site.js', 'favicon.svg']) fs.copyFileSync(path.join(site, f), path.join(out, f));
// DESIGN-040: the same fonts as the product (SIL OFL)
fs.mkdirSync(path.join(out, 'fonts'));
for (const f of fs.readdirSync(path.join(root, 'src/Core/i18n/fonts'))) if (f.endsWith('.woff2')) fs.copyFileSync(path.join(root, 'src/Core/i18n/fonts', f), path.join(out, 'fonts', f));

// the start page: the remembered choice → the region (time zone) → the browser → English. Same rules as the product (I18N-020).
const i18n = fs.readFileSync(path.join(root, 'src/Core/i18n/i18n.js'), 'utf8');
const zones = i18n.match(/const Z = (\{[\s\S]*?\});/)[1];
fs.writeFileSync(path.join(out, 'index.html'), `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${esc(cfg.product)}</title>
${Object.keys(LANGS).map((l) => `<link rel="alternate" hreflang="${l}" href="${esc(cfg.baseUrl)}/${l}/">`).join('\n')}
<script>
(function () {
  var L = ${JSON.stringify(Object.keys(LANGS))}, Z = ${zones};
  var norm = function (l) { l = String(l || '').toLowerCase().slice(0, 2); if (l === 'iw') l = 'he'; return L.indexOf(l) >= 0 ? l : null; };
  var saved = null; try { saved = norm(localStorage.getItem('obLang')); } catch (e) { }
  var nav = (navigator.languages || [navigator.language]).map(norm).filter(Boolean), tz = '';
  try { tz = Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { }
  var region = []; for (var k in Z) if (Z[k].split(' ').indexOf(tz) >= 0) region = k.split(' ');
  var pick = saved || (region.length > 1 ? region.filter(function (l) { return nav.indexOf(l) >= 0; })[0] || region[0] : region[0]) || nav[0] || 'en';
  location.replace(pick + '/');
})();
</script></head>
<body style="font-family:system-ui,sans-serif;padding:20px">${Object.entries(LANGS).map(([l, n]) => `<a href="${l}/" hreflang="${l}">${n}</a>`).join(' · ')}</body></html>
`);
fs.writeFileSync(path.join(out, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${Object.keys(LANGS).map((l) => `<url><loc>${esc(cfg.baseUrl)}/${l}/</loc></url>`).join('\n')}\n</urlset>\n`);
fs.writeFileSync(path.join(out, 'robots.txt'), `User-agent: *\nAllow: /\nSitemap: ${cfg.baseUrl}/sitemap.xml\n`);
console.log(Object.keys(LANGS).length + ' languages, ' + keys.length + ' texts → ' + out);
