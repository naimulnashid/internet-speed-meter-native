// Assembles the project site into site/dist for Vercel (Root Directory: site).
//
// The screenshots and the icon are copied from where the app keeps them
// rather than kept here too: tools\Snapshot.ps1 regenerates docs/screenshots,
// and a second copy would go stale the first time it did. Vercel's build sees
// the whole repository, so reaching outside the root directory works there as
// it does locally.

import { cpSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SITE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(SITE, '..');
const OUT = join(SITE, 'dist');
const SHOTS = join(ROOT, 'docs', 'screenshots');
const ICON = join(ROOT, 'src', 'SpeedMeter.App', 'Assets', 'app-icon.svg');

for (const need of [SHOTS, ICON]) {
  if (!existsSync(need)) {
    console.error(`Missing ${need}; the site would render with broken images.`);
    process.exit(1);
  }
}

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
for (const f of ['index.html', 'styles.css', 'site.js']) cpSync(join(SITE, f), join(OUT, f));
cpSync(ICON, join(OUT, 'favicon.svg'));
cpSync(SHOTS, join(OUT, 'screenshots'), { recursive: true });
console.log(`site built into ${OUT}`);
