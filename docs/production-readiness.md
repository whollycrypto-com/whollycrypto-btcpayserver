# 1.0 scope and deployment readiness

1.0 includes connection health, a searchable/paginated linked-payments list,
needs-review and error filters, notification/polling diagnostics, durable fast
IPN receipts and store-level network/asset selection. **Refund handoff is not
included.** Wallet keys, rates, scanners, gas and sweeps remain in Wholly.

A version number or passing fixtures is not certification of a merchant's
deployment. Only BTCPay 2.4.4 is exercised by the automated host. Complete the
following checks on your own staging installations before accepting live orders.

## Deployment checks

1. **Real staging payments and one-time fulfillment.** Test native ETH and an EVM
   stablecoin, one fast-finality network, partial/over/late payments, and BTC/LN
   alongside Wholly. Verify both dashboards and the consuming shop. Use only small,
   explicitly authorized amounts. Repeat signed notifications and lost responses.
2. **Recovery and security checks.** Restart both services mid-checkout, interrupt
   the API and IPN, rotate credentials with pending invoices, restore a backup,
   try another store's ID and test an invalid signature. Add active CI and an
   independent review of exact-amount accounting and the invoice state machine.
3. **Mobile and upgrade checks.** Test iOS/Android wallet apps, iframe allow/block,
   full-page return, 0.1/0.2 → 1.0 with pending invoices, and every BTCPay version
   claimed compatible. The current automated host is 2.4.4 only.

For broader distribution, submit a tested build to the official
[BTCPay Plugin Builder](https://docs.btcpayserver.org/Development/Plugins/#publishing-the-plugin)
for installation/update discovery, with documentation, logo and a short demo.
Manual GitHub installation works separately. Directory listing and automatic
plugin updates are not implied by this release or a successful package build.

Keep wallet keys, gas/sweeps, rates, chain scanners and asset management in Wholly.
Duplicating them inside BTCPay would add complexity without helping this connector.
