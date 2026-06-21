import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
const OUT = '/tmp/scaleshots/'; mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch();
for (const [w,h,name] of [[1440,900,'native'],[1152,720,'shrunk'],[1760,1100,'grown']]) {
  const page = await browser.newPage({ viewport: { width:w, height:h }, deviceScaleFactor:1 });
  await page.goto('file:///tmp/scaletest.html', { waitUntil:'load' });
  await page.waitForTimeout(400);
  const scale = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--frame-scale')));
  const box = await page.locator('.frame').first().boundingBox();
  const aspect = (box.width/box.height).toFixed(3);
  console.log(`${name} ${w}x${h}: scale=${scale.toFixed(4)} rendered=${Math.round(box.width)}x${Math.round(box.height)} aspect=${aspect} expectScale=${Math.min(w/1440,h/900).toFixed(4)}`);
  await page.screenshot({ path: OUT+name+'.png' });
  await page.close();
}
await browser.close();
