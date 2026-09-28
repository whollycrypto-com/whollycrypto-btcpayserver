// Synthetic iframe on the real packaged BTCPay host; no merchant API or funds.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const {chromium} = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.BTCPAY_TEST_URL;
const id = readFileSync(process.env.BTCPAY_TEST_INVOICE_FILE, 'utf8').trim();
assert.match(base || '', /^http:\/\/127\.0\.0\.1:\d+$/);
assert.match(id, /^[1-9A-HJ-NP-Za-km-z]{15,32}$/);
const browser = await chromium.launch({headless: true, args: ['--no-sandbox']});
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  const framePath = 'https://pay.example.com/invoice/33333333-3333-4333-8333-333333333333?embed=1';
  const path = base + '/plugins/whollycrypto/pay/' + id + '/embedded';
  // Intercept only the checkout fixture. No third-party API calls are allowed.
  await context.route('https://**/*', route => route.request().url() === framePath
    ? route.fulfill({status: 200, contentType: 'text/html', headers: {'Content-Security-Policy': `frame-ancestors ${base}`},
      body: '<!doctype html><html><body><h1>Synthetic Wholly checkout</h1><p>Choose a network and asset.</p></body></html>'})
    : route.abort());
  const response = await page.goto(path);
  assert.equal(response.status(), 200);
  assert.match(response.headers()['content-security-policy'], /frame-src 'self' https:\/\/pay\.example\.com/);
  assert.match(response.headers()['content-security-policy'], /frame-ancestors 'none'/);
  assert.match(response.headers()['cache-control'], /no-store/);
  assert.equal(response.headers()['x-frame-options'], 'DENY');
  await page.frameLocator('#wholly-checkout-frame').getByText('Synthetic Wholly checkout').waitFor();
  const fallback = page.getByRole('link', {name: 'Open full checkout'});
  assert.equal(await fallback.getAttribute('href'), framePath.replace('?embed=1', ''));
  assert.equal(await fallback.getAttribute('target'), '_blank');
  assert.equal(await page.locator('#wholly-checkout-frame').getAttribute('src'), framePath);
  for (const width of [1440, 768, 390, 320]) {
    await page.setViewportSize({width, height: 900});
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `No overflow at ${width}`);
    if (process.env.BTCPAY_TEST_SCREENSHOTS) await page.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/embedded-${width}.png`, fullPage: true});
  }
  // Forged checkout-origin message cannot redirect the parent or settle anything.
  await page.frames().find(f => f.url() === framePath).evaluate(() => parent.postMessage({type: 'wholly:btcpay-return', status: 'settled'}, '*'));
  await page.evaluate(() => window.postMessage({type: 'wholly:btcpay-return'}, location.origin));
  await page.waitForTimeout(200);
  assert.equal(page.url(), path, 'Both origin and source must match for return messages');
  assert.deepEqual(await (await context.request.get(base + '/plugins/whollycrypto/pay/' + id + '/status')).json(), {returnToInvoice: false});
  // Loading our same-origin return page inside the frame exits to BTCPay, not a success assertion.
  await page.evaluate(url => { document.getElementById('wholly-checkout-frame').src = url; }, base + '/plugins/whollycrypto/return/' + id);
  await page.waitForURL(base + '/i/' + id);
  // Frame-denied installations still get a working full-page fallback.
  await context.unroute('https://**/*');
  await context.route('https://**/*', route => route.request().url() === framePath
    ? route.fulfill({status: 200, contentType: 'text/html', headers: {'Content-Security-Policy': "frame-ancestors 'none'"}, body: '<h1>Must not be framed</h1>'}) : route.abort());
  await page.goto(path);
  await fallback.waitFor({state: 'visible'});
  assert.equal(await fallback.getAttribute('href'), framePath.replace('?embed=1', ''));
  assert.deepEqual(errors, []);
  console.log('PASS: embedded checkout/CSP, full-page fallback, responsive layout, origin/source rejection, safe same-origin return and read-only status. Remote checkout is synthetic.');
} finally { await browser.close(); }
