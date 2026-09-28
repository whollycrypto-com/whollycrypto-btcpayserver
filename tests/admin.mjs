// Operator UI against a disposable host prepared by --seed-embedded-browser.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const {chromium} = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.BTCPAY_TEST_URL;
assert.match(base || '', /^http:\/\/127\.0\.0\.1:\d+$/);
const browser = await chromium.launch({headless: true, args: ['--no-sandbox']});
try {
  const context = await browser.newContext({storageState: process.env.BTCPAY_TEST_STATE});
  const page = await context.newPage();
  const submitSearch = () => page.locator('#mainContent').getByRole('button', {name: 'Search', exact: true}).click();
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.goto(base + '/');
  const path = await page.locator('#Nav-Plugins a[href$="/whollycrypto"]').getAttribute('href');
  assert.ok(path);
  await page.goto(base + path);
  await page.getByRole('heading', {name: 'Connection health'}).waitFor();
  await page.getByText('✓ Read access verified', {exact: true}).waitFor();
  await page.getByText('✓ Successful API creation observed', {exact: true}).waitFor();
  assert.equal(await page.locator('#ApiKey').inputValue(), '');
  await page.locator('#LimitMethods').check();
  assert.ok(await page.locator('#wholly-method-picker').isVisible());
  assert.equal(await page.locator('.wholly-method-row:visible').count(), 2);
  await page.locator('#wholly-method-search').fill('usdc');
  assert.equal(await page.locator('.wholly-method-row:visible').count(), 1);
  await page.locator('input[name="SelectedMethods"][value="44444444-4444-4444-8444-444444444444"]').check();
  await page.locator('#wholly-method-search').fill('lightning');
  assert.equal(await page.locator('.wholly-method-row:visible').count(), 1);
  await page.locator('#wholly-method-search').fill('');
  assert.ok(await page.locator('input[name="SelectedMethods"][value="44444444-4444-4444-8444-444444444444"]').isChecked(), 'Search never clears a selection');
  // Never submit this form: its API origin is synthetic.
  for (const width of [1440, 390]) {
    await page.setViewportSize({width, height: 950});
    await page.reload();
    if (width < 600 && await page.locator('#mainNav.show').count()) {
      await page.locator('#mainMenuToggle').click();
      await page.waitForFunction(() => !document.getElementById('mainNav').classList.contains('show'));
    }
    await page.locator('#LimitMethods').check();
    await page.locator('input[name="SelectedMethods"][value="44444444-4444-4444-8444-444444444444"]').check();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    if (process.env.BTCPAY_TEST_SCREENSHOTS) await page.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/health-assets-${width}.png`, fullPage: true});
  }
  await page.getByRole('link', {name: 'Linked payments', exact: true}).click();
  await page.getByText('Page 1 of 2 · 25 total').waitFor();
  assert.equal(await page.locator('tbody tr').count(), 20);
  assert.ok(await page.locator('[aria-disabled="true"]').filter({hasText: 'Previous'}).count());
  await page.getByRole('link', {name: 'Next', exact: true}).click();
  await page.getByText('Page 2 of 2 · 25 total').waitFor();
  assert.equal(await page.locator('tbody tr').count(), 5);
  await page.locator('#search').fill('synthetic-list-order-3');
  await submitSearch();
  assert.equal(await page.locator('tbody tr').count(), 1);
  await page.locator('summary').filter({hasText: 'Needs review'}).click();
  await page.getByText('Synthetic late payment. Verify before fulfillment.').waitFor();
  await page.locator('#search').fill(''); await page.locator('#filter').selectOption('errors');
  await submitSearch();
  assert.equal(await page.locator('tbody tr').count(), 1);
  await page.locator('summary').filter({hasText: 'Verification error'}).click();
  await page.getByText('Wholly API HTTP 429: wait for the retry deadline.').waitFor();
  await page.locator('#filter').selectOption('all');
  const invoice = readFileSync(process.env.BTCPAY_TEST_INVOICE_FILE, 'utf8').trim();
  await page.locator('#search').fill(invoice);
  await submitSearch();
  assert.equal(await page.locator('tbody tr').count(), 1);
  await page.locator('#search').fill('33333333-3333-4333-8333-333333333333');
  await submitSearch();
  assert.equal(await page.locator('tbody tr').count(), 1);
  assert.ok(!(await page.content()).includes('synthetic-api-credential-for-tests'));
  assert.ok(!(await page.content()).includes('RequestJson'));
  await page.locator('#search').fill('');
  await submitSearch();
  for (const width of [1440, 390]) {
    await page.setViewportSize({width, height: 950});
    await page.reload();
    if (width < 600 && await page.locator('#mainNav.show').count()) {
      await page.locator('#mainMenuToggle').click();
      await page.waitForFunction(() => !document.getElementById('mainNav').classList.contains('show'));
    }
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Table scroll stays inside its wrapper');
    if (process.env.BTCPAY_TEST_SCREENSHOTS) await page.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/payments-${width}.png`, fullPage: true});
  }
  const anonymous = await browser.newContext();
  assert.ok([302, 401, 403].includes((await anonymous.request.get(base + path + '/payments', {maxRedirects: 0})).status()));
  assert.equal((await context.request.post(base + path + '/invoices/' + invoice + '/refresh', {form: {returnToPayments: 'true'}, maxRedirects: 0})).status(), 400);
  assert.deepEqual(await (await anonymous.request.get(base + '/plugins/whollycrypto/health')).json(), {service: 'Wholly Crypto connector', callback_requires_signature: true});
  await anonymous.close();
  assert.deepEqual(errors, []);
  console.log('PASS: 1.0 health, asset search/selection, responsive paginated payments, filters, all ID searches, hidden secrets, CSRF and anonymous boundaries.');
} finally { await browser.close(); }
