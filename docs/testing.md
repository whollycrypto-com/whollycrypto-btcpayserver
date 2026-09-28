# Build and verification

Requirements: Git, a patched .NET 10 SDK, Python 3 for package audits, and
PostgreSQL for the integration suite. No node, wallet or chain transaction is used.

```sh
git clone --recurse-submodules https://github.com/whollycrypto-com/whollycrypto-btcpayserver.git
cd whollycrypto-btcpayserver
dotnet build tests/WhollyCrypto.Tests.csproj -c Release
dotnet tests/bin/Release/net10.0/WhollyCrypto.Tests.dll
```

The BTCPay submodule is pinned to v2.4.4, commit
`2d5a0d8077bb33af080e949031da33d84b80638d`. Do not substitute `master` silently.

For integration tests, create a **disposable** PostgreSQL database whose name
starts with `wholly_btcpay_test_`. The guard refuses other database names.
Use a test role with rights only to that database and set `WHOLLY_TEST_DATABASE`
through your test environment, not a tracked file. Then:

```sh
dotnet tests/bin/Release/net10.0/WhollyCrypto.Tests.dll --database
```

This applies actual BTCPay migrations and exercises its repositories, encrypted
store settings, advisory locks, PaymentService and live InvoiceWatcher with
synthetic Wholly API responses. It does not contact a live merchant or send funds.
Use a fresh database for each run; drop only that explicit test database afterward.

The suite covers exact decimal validation, SSRF origins/addresses, raw-body HMAC,
tampering/replay, stale/mismatched invoice state, duplicate concurrent callbacks,
partial/full/late/manual/reorg and competing-method payments, lost-response
recovery, original-fiat accounting and upstream invoice-status transitions.

`tests/browser.mjs` exercises the compiled package in a real, disposable BTCPay
2.4.4 web host: initial account/store, settings save, blank secret fields, small
screens, anonymous access, CSRF, unsigned callbacks, embedded assets, local invoice
creation and the customer payment form. Install Playwright and Chromium separately,
set `BTCPAY_TEST_URL` to the **loopback-only** test host, then run it with Node.
It refuses non-loopback hosts. Optional `BTCPAY_TEST_STATE` and
`BTCPAY_TEST_SCREENSHOTS` paths must be outside the repository; delete the synthetic
session state after testing. No outbound Wholly request or real payment is made.

For the iframe browser fixture, set `BTCPAY_TEST_INVOICE_FILE` to an out-of-repo
temporary path when running `tests/browser.mjs`. With the same disposable database,
run the test executable with `--seed-embedded-browser <ID from that file>` and then
`node tests/embedded.mjs`. The seed refuses non-test databases or non-synthetic
orders. It writes only a fake linked checkout to that one fixture invoice.
Playwright intercepts the checkout origin; no remote Wholly requests are made.
Tests cover CSP/origin boundaries, blocked-frame fallback, server-only status,
same-origin return and 320/390/768/1440px layouts. Real wallet apps and a real
Wholly allowed-origin policy still need staging verification.

Package using the pinned upstream PluginPacker:

```sh
bash tools/package.sh
```

Only the connector DLL, dependency manifest and license are allowed in the
`.btcpay` package. Razor views and icons are embedded. No upstream server binary,
PDB, private processor source, runtime config or data is shipped. `tools/audit.py`
checks the source boundary and package entries. The optional `ci/tests.yml`
template can be enabled as `.github/workflows/tests.yml` by a repository admin.
Packaging disables the shared compiler process to avoid retaining Razor compiler
state between runs on long-lived build hosts; normal dependency caches are reused.

Upstream v2.4.4 references `Microsoft.Build.Tasks.Git` 8.0.0 and restore reports
[GHSA-23fw-v26w-5fgq](https://github.com/advisories/GHSA-23fw-v26w-5fgq).
This is an upstream build-time dependency, not a DLL shipped with this connector.
Do not suppress this warning or build from repositories containing credentials in
Git URLs. Use a patched SDK and review the upstream dependency on future updates.

## Deployment staging checklist

Use your test BTCPay and Wholly installations, synthetic customer information and
a deliberately small payment you authorize. Do not run this against customer orders.

1. Upload the package, restart and verify the plugin/settings link loads. Check
   narrow/mobile checkout and light/dark appearance. Verify Bitcoin/Lightning unchanged.
2. An anonymous user and a user of another store cannot read or change settings.
   Secret values do not appear in page source. POST without CSRF is rejected.
3. Read test succeeds with the restricted credential. Read-only, disabled and wrong
   store credentials must not produce a valid payable linked invoice.
4. Create a fixed fiat BTCPay invoice. No remote invoice exists before selection.
   Select Wholly, check original amount/currency, return links and accepted assets.
5. Double-click and reload. One linked invoice remains. Simulate a lost response
   and restart BTCPay: retry must reuse the exact idempotency key and request.
6. Observe processing then settlement and **one** fulfillment. Repeat signed
   callbacks and send malformed/unsigned callbacks: no duplicate credit.
7. Verify partial, expired/late, manual override and double-method payment review.
   A browser return, API outage or bad signature must never settle an order.
8. Disable IPN temporarily in staging: pending polling reconciles an actual paid
   invoice. Restore IPN. Check API rate limiting/backoff and error presentation.
9. Change connection settings. Old pending invoices still use the original
   connection. Disable the method: new orders must not start Wholly payments.
10. Back up and restore database plus data-protection keys. Confirm existing
    invoices can still be verified. Review refund handling before live use.
11. Upload 0.2.0 over 0.1.0 with pending invoices: original full-page connections
    and saved requests must remain unchanged. Enable iframe for a new connection
    and invoice. Test correct/missing allowed origins, full-page fallback, wallet
    deep links, explicit return, settlement and mobile Safari/Chrome.

Automated fixtures do not replace this real deployment checklist or security review.
