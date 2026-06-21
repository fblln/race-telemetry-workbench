// Verifies the launcher shows correct per-season session counts on first load (no click).
// Regression guard for the bug where non-selected seasons showed "0 sessions" until clicked.
import { chromium } from 'playwright';

const BASE = process.env.BASE_URL ?? 'http://localhost:5170';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
try {
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.locator('.selchip').first().waitFor({ state: 'visible', timeout: 20000 });
  await page.waitForTimeout(800); // let counts render after sessions load

  const chips = await page.locator('.selchip').allInnerTexts();
  console.log('season chips:', JSON.stringify(chips));

  // Expect a chip per season with a non-zero count, including the non-selected ones.
  const counts = Object.fromEntries(chips.map(t => {
    const m = t.match(/(\d{4})\D+(\d+)\s+sessions/s);
    return m ? [m[1], Number(m[2])] : ['?', NaN];
  }));
  console.log('parsed:', JSON.stringify(counts));

  const expected = { '2024': 1, '2025': 24, '2026': 7 };
  let ok = true;
  for (const [yr, n] of Object.entries(expected)) {
    if (counts[yr] !== n) { console.log(`✗ ${yr}: expected ${n}, got ${counts[yr]}`); ok = false; }
    else console.log(`✓ ${yr}: ${n} sessions`);
  }
  process.exitCode = ok ? 0 : 1;
  console.log(ok ? 'PASS' : 'FAIL');
} catch (e) {
  console.error('FAILED:', e.message);
  process.exitCode = 1;
} finally {
  await browser.close();
}
