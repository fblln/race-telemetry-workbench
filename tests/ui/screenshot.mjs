// Drives the Blazor Server UI harness (the SAME components the MAUI app hosts) in headless
// Chromium and screenshots the launcher + Reports & AI views. Lets us self-verify the DOM,
// which maui-devflow can't reach inside the WKWebView.
//
// Usage: node screenshot.mjs [circuit]
//   BASE_URL   default http://localhost:5170
//   circuit    default "Budapest"
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.BASE_URL ?? 'http://localhost:5170';
const CIRCUIT = process.argv[2] ?? 'Budapest';
const OUT = new URL('./shots/', import.meta.url).pathname;
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('console', m => { if (m.type() === 'error') console.log('[browser error]', m.text()); });

try {
  console.log('→ open', BASE);
  await page.goto(BASE, { waitUntil: 'networkidle' });

  // Funnel loads sessions on init; wait for circuit cards.
  await page.locator('.ccard').first().waitFor({ state: 'visible', timeout: 20000 });
  // Wait for the Blazor Server circuit to connect (otherwise @onclick handlers are inert).
  await page.waitForFunction(() => window.Blazor && document.documentElement.classList.length >= 0, { timeout: 20000 });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: OUT + 'launcher.png' });
  console.log('✓ launcher.png');

  // Pick a circuit → opens session + loads drivers (all selected by default).
  console.log('→ pick circuit', CIRCUIT);
  await page.locator('.ccard', { hasText: CIRCUIT }).first().click();
  await page.locator('.dchip').first().waitFor({ state: 'visible', timeout: 20000 });
  await page.waitForTimeout(600);
  await page.screenshot({ path: OUT + 'launcher-selected.png' });
  console.log('✓ launcher-selected.png');

  // Open the report (button → Reports & AI view 1).
  console.log('→ open report');
  await page.getByRole('button', { name: /Open report/i }).click();

  // Reports & AI: wait for the real-data race summary panel + real winner (not the "—" placeholder).
  await page.getByText('Race summary').waitFor({ state: 'visible', timeout: 20000 });
  const winnerCell = page.locator('.ro', { hasText: 'Winner' }).locator('.v');
  await winnerCell.filter({ hasNotText: '—' }).waitFor({ state: 'visible', timeout: 20000 }).catch(() => {});
  await page.waitForTimeout(7000); // let the slow incidents aggregate fill in too
  await page.screenshot({ path: OUT + 'reports.png' });
  console.log('✓ reports.png');

  // Capture the rendered summary text so we can confirm it's real data, not mock.
  const winner = await page.locator('.ro', { hasText: 'Winner' }).locator('.v').innerText().catch(() => '?');
  const fastest = await page.locator('.ro', { hasText: 'Fastest lap' }).locator('.v').innerText().catch(() => '?');
  console.log(`summary → winner=${winner.trim()} fastestLap=${fastest.trim()}`);
} catch (e) {
  console.error('FAILED:', e.message);
  await page.screenshot({ path: OUT + 'failure.png' }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
