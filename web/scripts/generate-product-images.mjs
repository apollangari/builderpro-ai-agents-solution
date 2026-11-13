// Generates the sample product images in web/public/products from the Catalog dev seed (2i).
//
//   node scripts/generate-product-images.mjs
//
// Every image is drawn here from the product's category, colour and attributes: no photos copied
// from the web (licensing), deterministic output (same seed -> same SVG), small files.
// Retailers replace these with real photography later; the ImageUrl field stays the same.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const seedFile = join(here, '..', '..', 'src', 'Services', 'Catalog', 'BuilderPro.Catalog.Api', 'appsettings.Development.json');
const outDir = join(here, '..', 'public', 'products');

// The seed is JSON with // comments.
const seed = JSON.parse(readFileSync(seedFile, 'utf8').replace(/^\s*\/\/.*$/gm, ''));
const products = seed.Catalog.DevProducts.filter((p) => p.ImageUrl);

// ---------- colours ----------
const NAMED = [
  [/calacatta|bright white|white/i, '#f4f1ea'],
  [/absolute black|black/i, '#23252a'],
  [/charcoal/i, '#3b3d42'],
  [/estate grey|harbor grey|concrete grey|grey|gray/i, '#8a8d91'],
  [/weathered wood/i, '#7a6a58'],
  [/carrara/i, '#e9e7e2'],
  [/maple/i, '#d7a86e'],
  [/natural oak|oak/i, '#c49a6c'],
  [/sand beige|beige/i, '#d8c7a6'],
  [/navy/i, '#1f2f57'],
  [/sage/i, '#9caf88'],
  [/cedar/i, '#a8643a'],
];
const colorOf = (p) => {
  const c = `${p.Attributes?.color ?? ''} ${p.Name}`;
  for (const [re, hex] of NAMED) if (re.test(c)) return hex;
  return { wood: '#b07a48', resin: '#7c8b74', metal: '#9aa3a8', vinyl: '#f2f2ee', composite: '#8a8d91' }[p.Attributes?.material] ?? '#b9b2a4';
};
const shade = (hex, amt) => {
  const n = parseInt(hex.slice(1), 16);
  const ch = (v) => Math.max(0, Math.min(255, Math.round(v + amt * 255)));
  return '#' + [ch(n >> 16), ch((n >> 8) & 255), ch(n & 255)].map((v) => v.toString(16).padStart(2, '0')).join('');
};
const isDark = (hex) => {
  const n = parseInt(hex.slice(1), 16);
  return (0.299 * (n >> 16) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) < 110;
};

// Deterministic pseudo-random per SKU, so regenerating gives identical files.
const rng = (seedText) => {
  let h = 2166136261;
  for (const ch of seedText) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return () => ((h = Math.imul(h ^ (h >>> 15), 2246822507) ^ Math.imul(h ^ (h >>> 13), 3266489909)) >>> 0) / 4294967296;
};

const W = 400, H = 300;
// bg = null: transparent, for products the visualizer places as objects in a scene (sheds, 2l).
const frame = (inner, bg = '#f5f1e8') =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" role="img">` +
  (bg ? `<rect width="${W}" height="${H}" fill="${bg}"/>` : '') +
  `${inner}</svg>\n`;

// ---------- drawings per category ----------
const draw = {
  countertops(p, r) {
    const base = colorOf(p);
    const m = p.Attributes.material;
    if (m === 'butcher-block') {
      let strips = '';
      for (let x = 0; x < W; x += 26) strips += `<rect x="${x}" y="0" width="26" height="${H}" fill="${shade(base, (r() - 0.5) * 0.12)}"/>`;
      return frame(strips + `<rect width="${W}" height="${H}" fill="url(#g)" opacity=".15"/><defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient></defs>`);
    }
    // Stone: base + turbulence speckle + a few veins.
    const vein = isDark(base) ? shade(base, 0.18) : shade(base, -0.28);
    let veins = '';
    const count = m === 'granite' ? 0 : 4;
    for (let i = 0; i < count; i++) {
      const y = r() * H, d = `M -20 ${y} C ${W * 0.3} ${y + (r() - 0.5) * 160}, ${W * 0.6} ${y + (r() - 0.5) * 160}, ${W + 20} ${y + (r() - 0.5) * 120}`;
      veins += `<path d="${d}" fill="none" stroke="${vein}" stroke-width="${1 + r() * 2.5}" opacity=".55"/>`;
    }
    const speckle = m === 'granite' ? 0.9 : m === 'laminate' ? 0.25 : 0.45;
    return frame(
      `<defs><filter id="n"><feTurbulence type="fractalNoise" baseFrequency="${m === 'granite' ? 0.9 : 0.6}" numOctaves="2" seed="${Math.floor(r() * 99)}"/>` +
        `<feColorMatrix values="0 0 0 0 ${isDark(base) ? 1 : 0}  0 0 0 0 ${isDark(base) ? 1 : 0}  0 0 0 0 ${isDark(base) ? 1 : 0}  0 0 0 ${speckle * 0.35} 0"/></filter></defs>` +
        `<rect width="${W}" height="${H}" fill="${base}"/><rect width="${W}" height="${H}" filter="url(#n)"/>${veins}`,
      base,
    );
  },
  roofing(p, r) {
    const base = colorOf(p);
    const t = p.Attributes.type;
    if (t === 'underlayment') {
      return frame(`<rect x="70" y="90" width="260" height="120" rx="10" fill="#d9d3c4"/><ellipse cx="70" cy="150" rx="28" ry="60" fill="#c8c1b0"/><ellipse cx="70" cy="150" rx="10" ry="22" fill="#8a8473"/>` +
        `<text x="200" y="158" text-anchor="middle" font-family="Inter,sans-serif" font-size="18" font-weight="600" fill="#14213d">SYNTHETIC</text>`);
    }
    if (t === 'flashing') {
      return frame(`<path d="M40 120 L360 120 L360 150 L60 150 L40 190 Z" fill="#e8e8e4" stroke="#b5b5ad" stroke-width="3"/>`);
    }
    // Shingles: offset courses; architectural = varied tab widths and shades.
    let rows = '';
    const arch = p.Attributes.style === 'architectural';
    for (let y = 0, row = 0; y < H; y += 34, row++) {
      let x = row % 2 ? -30 : 0;
      while (x < W) {
        const w = arch ? 40 + r() * 45 : 60;
        rows += `<rect x="${x + 1}" y="${y}" width="${w - 2}" height="32" rx="2" fill="${shade(base, arch ? (r() - 0.5) * 0.16 : (r() - 0.5) * 0.05)}"/>`;
        x += w;
      }
      rows += `<rect x="0" y="${y + 30}" width="${W}" height="4" fill="${shade(base, -0.18)}"/>`;
    }
    return frame(rows, shade(base, -0.2));
  },
  sheds(p) {
    const wall = colorOf(p), roof = shade(wall, -0.25);
    const gambrel = p.Attributes.roofStyle === 'gambrel';
    const roofPath = gambrel ? 'M90 150 L120 95 L200 70 L280 95 L310 150 Z' : 'M80 150 L200 70 L320 150 Z';
    const boards = p.Attributes.material === 'metal'
      ? Array.from({ length: 11 }, (_, i) => `<line x1="${105 + i * 19}" y1="150" x2="${105 + i * 19}" y2="250" stroke="${shade(wall, -0.12)}" stroke-width="2"/>`).join('')
      : Array.from({ length: 5 }, (_, i) => `<line x1="100" y1="${168 + i * 18}" x2="300" y2="${168 + i * 18}" stroke="${shade(wall, -0.12)}" stroke-width="2"/>`).join('');
    // Transparent and no lawn: the visualizer's backyard supplies the ground (2l).
    return frame(`<rect x="100" y="150" width="200" height="100" fill="${wall}"/>${boards}` +
      `<path d="${roofPath}" fill="${roof}"/><rect x="175" y="185" width="50" height="65" fill="${shade(wall, -0.3)}"/><rect x="120" y="170" width="34" height="26" fill="#cfe1ea" stroke="#fff" stroke-width="3"/>`, null);
  },
  flooring(p, r) {
    const base = colorOf(p);
    let g = '';
    if (p.Attributes.material === 'tile') {
      for (let y = 0; y < H; y += 75) for (let x = 0; x < W; x += 75) g += `<rect x="${x + 2}" y="${y + 2}" width="71" height="71" fill="${shade(base, (r() - 0.5) * 0.06)}"/>`;
      return frame(g, shade(base, -0.25));
    }
    for (let y = 0, row = 0; y < H; y += 38, row++) {
      let x = -r() * 120;
      while (x < W) {
        const w = 120 + r() * 110;
        g += `<rect x="${x + 1}" y="${y + 1}" width="${w - 2}" height="36" fill="${shade(base, (r() - 0.5) * 0.14)}"/>`;
        x += w;
      }
    }
    return frame(g, shade(base, -0.3));
  },
  decking(p, r) {
    const base = colorOf(p);
    let g = '';
    for (let y = 10; y < H; y += 46) g += `<rect x="0" y="${y}" width="${W}" height="38" rx="3" fill="${shade(base, (r() - 0.5) * 0.12)}"/>`;
    return frame(g, '#5b4a3a');
  },
  cabinets(p) {
    const c = colorOf(p), edge = shade(c, isDark(c) ? 0.12 : -0.12);
    const shaker = p.Attributes.style === 'shaker';
    const door = (x) => `<rect x="${x}" y="60" width="118" height="190" rx="4" fill="${c}" stroke="${edge}" stroke-width="3"/>` +
      (shaker ? `<rect x="${x + 16}" y="76" width="86" height="158" fill="none" stroke="${edge}" stroke-width="3"/>` : '') +
      `<rect x="${x + (x < 200 ? 98 : 10)}" y="140" width="8" height="34" rx="3" fill="#b9a77e"/>`;
    return frame(`<rect x="72" y="40" width="256" height="226" rx="6" fill="${shade(c, -0.08)}"/>${door(80)}${door(202)}<rect x="60" y="30" width="280" height="14" fill="#e9e7e2"/>`);
  },
  paint(p) {
    const c = colorOf(p);
    return frame(`<rect x="0" y="0" width="${W}" height="${H}" fill="${c}" opacity=".35"/>` +
      `<ellipse cx="200" cy="90" rx="90" ry="22" fill="#c9cdd2"/><rect x="110" y="90" width="180" height="150" fill="#dfe2e6"/><ellipse cx="200" cy="240" rx="90" ry="22" fill="#c9cdd2"/>` +
      `<rect x="110" y="130" width="180" height="70" fill="${c}"/><path d="M110 90 Q200 30 290 90" fill="none" stroke="#8a8d91" stroke-width="5"/>` +
      `<text x="200" y="172" text-anchor="middle" font-family="Inter,sans-serif" font-size="16" font-weight="600" fill="${isDark(c) ? '#fff' : '#14213d'}">${p.Attributes.finish}</text>`);
  },
  'windows-doors'(p) {
    if (p.Attributes.type === 'entry-door') {
      return frame(`<rect x="0" y="0" width="${W}" height="${H}" fill="#e7dfcf"/><rect x="140" y="30" width="120" height="240" fill="#2b4a7a"/>` +
        `<rect x="156" y="50" width="88" height="80" fill="#3d5f94"/><rect x="156" y="146" width="88" height="104" fill="#3d5f94"/><circle cx="244" cy="150" r="6" fill="#d4b26a"/>`);
    }
    return frame(`<rect x="0" y="0" width="${W}" height="${H}" fill="#e7dfcf"/><rect x="120" y="30" width="160" height="240" fill="#fafaf7" stroke="#d6cdb8" stroke-width="4"/>` +
      `<rect x="134" y="44" width="132" height="98" fill="#bcd7e4"/><rect x="134" y="156" width="132" height="98" fill="#bcd7e4"/><rect x="120" y="144" width="160" height="10" fill="#fafaf7"/>`);
  },
  fencing(p, r) {
    const c = colorOf(p);
    let pickets = '';
    // One full-width panel on a transparent background, so the visualizer can line panels up (2l).
    for (let x = 0; x < W; x += 30) pickets += `<path d="M${x + 2} 300 L${x + 2} 70 L${x + 15} 55 L${x + 28} 70 L${x + 28} 300 Z" fill="${shade(c, (r() - 0.5) * 0.1)}"/>`;
    return frame(`${pickets}<rect x="0" y="120" width="${W}" height="12" fill="${shade(c, -0.2)}"/><rect x="0" y="230" width="${W}" height="12" fill="${shade(c, -0.2)}"/>`, null);
  },
};

mkdirSync(outDir, { recursive: true });
const done = new Set();
for (const p of products) {
  const file = p.ImageUrl.replace(/^\/products\//, '');
  if (done.has(file)) continue; // same image for the same SKU across tenants
  const fn = draw[p.Category];
  if (!fn) throw new Error(`No drawing for category ${p.Category} (${p.Sku})`);
  writeFileSync(join(outDir, file), fn(p, rng(p.Sku)));
  done.add(file);
}
console.log(`Wrote ${done.size} images to ${outDir}`);
