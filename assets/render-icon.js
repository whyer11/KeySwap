const sharp = require('sharp');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const svg = path.join(root, 'assets', 'logo.svg');
const outDir = path.join(root, 'assets', 'png');
if (!fs.existsSync(outDir)) fs.mkdirSync(outDir, { recursive: true });

const sizes = [16, 24, 32, 48, 64, 128, 256];

(async () => {
  for (const s of sizes) {
    await sharp(svg, { density: 384 })
      .resize(s, s, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
      .png()
      .toFile(path.join(outDir, s + '.png'));
    console.log('rendered', s);
  }
})().catch(e => { console.error(e); process.exit(1); });
