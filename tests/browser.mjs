// Run only against a disposable, loopback-only BTCPay test server with no live data.
// Install Playwright separately; PLAYWRIGHT_MODULE may identify an existing local module.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {existsSync, writeFileSync} from 'node:fs';
const {chromium} = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.BTCPAY_TEST_URL;
assert.ok(base && /^http:\/\/127\.0\.0\.1:\d+$/.test(base), 'Disposable loopback BTCPay required');
const browser = await chromium.launch({headless: true, args: ['--no-sandbox']});
try {
  const state = process.env.BTCPAY_TEST_STATE;
  const context = await browser.newContext(state && existsSync(state) ? {storageState: state} : {});
  const page = await context.newPage();
  page.setDefaultTimeout(15000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base + '/');
  if (new URL(page.url()).pathname.includes('register')) {
    const password = randomBytes(24).toString('base64url') + '!9aA';
    await page.locator('#Email').fill('btcpay-fixture@example.invalid');
    await page.locator('#Password').fill(password);
    await page.locator('#ConfirmPassword').fill(password);
    await Promise.all([page.waitForURL(u => !u.pathname.includes('register')), page.locator('#RegisterButton').click()]);
  }
  if (state) await context.storageState({path: state});
  if (new URL(page.url()).pathname === '/stores/create') {
    await page.locator('#Name').fill('Wholly connector staging');
    await page.locator('#DefaultCurrency').fill('EUR');
    await Promise.all([page.waitForURL(u => u.pathname !== '/stores/create'), page.locator('#Create').click()]);
  }
  if (!(await page.locator('a[href$="/whollycrypto"]').count())) {
    await page.locator('#mainNav a[href$="/settings"]').first().click();
  }
  const link = page.locator('a[href$="/whollycrypto"]').first();
  assert.ok(await page.locator('#Nav-Plugins a[href$="/whollycrypto"]').count(), 'Visible under Plugins after selecting store');
  assert.ok(await page.locator('#Nav-Plugins a[href$="/whollycrypto"]').isVisible(), 'Plugins entry is not just hidden markup');
  const settingsPath = await link.getAttribute('href');
  assert.ok(settingsPath);
  await page.locator('#Nav-Plugins a[href$="/whollycrypto"]').click();
  assert.equal((await page.goto(base + settingsPath)).status(), 200);
  if (!(await page.locator('details').first().evaluate(el => el.open))) await page.locator('details summary').first().click();
  await page.getByText('Save connection first', {exact: true}).waitFor();
  assert.ok(await page.locator('h2 img[alt="Wholly Crypto"]').count());
  await page.locator('#ApiOrigin').fill('https://api.example.com');
  await page.locator('#CheckoutOrigin').fill('https://pay.example.com');
  await page.locator('#ProjectId').fill('11111111-1111-4111-8111-111111111111');
  await page.locator('#WhollyStoreId').fill('22222222-2222-4222-8222-222222222222');
  await page.locator('#ApiKey').fill('synthetic-api-credential-for-tests');
  await page.locator('#IpnSecret').fill('synthetic-ipn-secret-for-tests');
  await page.locator('#Enabled').check();
  await page.locator('#EmbedCheckout').selectOption('true');
  await Promise.all([page.waitForResponse(r => r.url().endsWith(settingsPath) && r.request().method() === 'POST'), page.getByRole('button', {name: 'Save connection', exact: true}).click()]);
  await page.getByText('Connection saved.', {exact: false}).waitFor();
  assert.equal(await page.locator('#ApiKey').inputValue(), '');
  assert.equal(await page.locator('#IpnSecret').inputValue(), '');
  assert.equal(await page.locator('#EmbedCheckout').inputValue(), 'true');
  assert.equal(await page.locator('details').first().getAttribute('open'), null, 'Saved guide initially collapsed');
  assert.ok(!(await page.content()).includes('synthetic-api-credential-for-tests'));
  for (const width of [1440, 390]) {
    await page.setViewportSize({width, height: 900});
    await page.reload();
    if (width < 600 && await page.locator('#mainNav.show').count()) {
      await page.locator('#mainMenuToggle').click();
      await page.waitForFunction(() => !document.getElementById('mainNav').classList.contains('show'));
    }
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    if (process.env.BTCPAY_TEST_SCREENSHOTS) await page.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/settings-${width}.png`, fullPage: true});
  }
  assert.equal((await context.request.post(base + settingsPath, {form: {Enabled: 'false'}, maxRedirects: 0})).status(), 400);
  const anonymous = await browser.newContext();
  const anonResult = await anonymous.request.get(base + settingsPath, {maxRedirects: 0});
  assert.ok([302, 401, 403].includes(anonResult.status()));
  assert.equal((await anonymous.request.post(base + '/plugins/whollycrypto/callback/123456789ABCDEFG', {data: '{}'})).status(), 401);
  assert.equal((await anonymous.request.get(base + '/Resources/img/whollycrypto.svg')).status(), 200);
  assert.equal((await anonymous.request.get(base + '/Resources/img/whollycrypto-horizontal-color.png')).status(), 200);
  assert.equal((await anonymous.request.get(base + '/Resources/css/whollycrypto.css')).status(), 200);
  // Simulate TLS termination at a trusted loopback proxy for a synthetic invoice.
  // This creates only a local BTCPay invoice, never a Wholly invoice or payment.
  const storePath = settingsPath.replace(/\/whollycrypto$/, '');
  await page.goto(base + storePath + '/invoices/create');
  await page.locator('#Amount').fill('25');
  await page.locator('#Currency').fill('EUR');
  await page.locator('#OrderId').fill('synthetic-browser-order');
  await page.setExtraHTTPHeaders({'X-Forwarded-Proto': 'https'});
  await Promise.all([page.waitForURL(u => !u.pathname.endsWith('/create')), page.locator('#page-primary').click()]);
  await page.setExtraHTTPHeaders({});
  const invoiceId = new URL(page.url()).pathname.split('/').at(-1);
  assert.match(invoiceId, /^[1-9A-HJ-NP-Za-km-z]{15,32}$/);
  if (process.env.BTCPAY_TEST_INVOICE_FILE) writeFileSync(process.env.BTCPAY_TEST_INVOICE_FILE, invoiceId, {mode: 0o600});
  assert.ok((await page.locator('body').innerText()).includes('Wholly Crypto'));
  const customer = await anonymous.newPage();
  await customer.goto(base + '/i/' + invoiceId);
  await customer.getByRole('button', {name: 'Continue with Wholly Crypto'}).waitFor();
  assert.ok(await customer.locator('img[alt="Wholly Crypto"]').count());
  assert.equal(await customer.locator('#AmountDue').innerText(), '25.00 EUR');
  assert.equal(await customer.locator('form').filter({has: customer.getByRole('button', {name: 'Continue with Wholly Crypto'})}).getAttribute('action'), '/plugins/whollycrypto/pay/' + invoiceId);
  // Returning from browser history must not leave the payment button disabled.
  // Cancel the actual POST: this test never creates a remote payment request.
  await customer.locator('form').filter({has: customer.getByRole('button', {name: 'Continue with Wholly Crypto'})}).evaluate(form => {
    form.addEventListener('submit', event => event.preventDefault(), {once: true});
  });
  await customer.getByRole('button', {name: 'Continue with Wholly Crypto'}).click();
  assert.ok(await customer.getByRole('button', {name: 'Opening checkout…'}).isDisabled());
  await customer.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', {persisted: true})));
  assert.ok(await customer.getByRole('button', {name: 'Continue with Wholly Crypto'}).isEnabled());
  const landing = await anonymous.newPage();
  assert.equal((await landing.goto(base + '/plugins/whollycrypto/pay/' + invoiceId)).status(), 200);
  assert.ok((await landing.locator('body').innerText()).includes('25 EUR'));
  assert.ok(await landing.locator('input[name="__RequestVerificationToken"]').count());
  assert.equal((await anonymous.request.post(base + '/plugins/whollycrypto/pay/' + invoiceId, {data: {}})).status(), 400);
  assert.equal((await anonymous.request.get(base + '/plugins/whollycrypto/pay/' + invoiceId + '/embedded')).status(), 404, 'Cannot embed an unlinked invoice');
  const cardResponse = await page.goto(base + '/server/plugins');
  assert.equal(cardResponse.status(), 200);
  const card = page.locator('[id="BTCPayServer.Plugins.WhollyCrypto"]');
  assert.ok((await card.innerText()).includes('Plugins → Wholly Crypto'));
  assert.ok(!(await card.innerText()).includes('No documentation'));
  assert.equal(await card.getByRole('link', {name: 'Details', exact: true}).getAttribute('href'), 'https://github.com/whollycrypto-com/whollycrypto-btcpayserver#setup');
  if (process.env.BTCPAY_TEST_SCREENSHOTS) await card.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/installed-plugin.png`});
  if (process.env.BTCPAY_TEST_SCREENSHOTS) await customer.screenshot({path: `${process.env.BTCPAY_TEST_SCREENSHOTS}/checkout.png`, fullPage: true});
  await anonymous.close();
  assert.deepEqual(errors, []);
  console.log('PASS: packaged plugin loads; store navigation, saved settings, hidden secrets, mobile layout, CSRF, anonymous permissions, signed-callback gate, embedded assets, local invoice creation and customer checkout.');
} finally { await browser.close(); }
