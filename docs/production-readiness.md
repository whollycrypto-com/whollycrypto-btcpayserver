# A useful 1.0, without adding complexity

0.2.0 is a preview. Passing connector tests is not proof of a completed payment
between two real installations. The following are recommendations, not implemented
features or production-readiness claims.

## Release gates

1. **Real staging payments and one-time fulfillment.** Test native ETH and an EVM
   stablecoin, one fast-finality network, partial/over/late payments, and BTC/LN
   alongside Wholly. Verify both dashboards and the consuming shop. Use only small,
   explicitly authorized amounts. Repeat signed notifications and lost responses.
2. **Recovery and security checks.** Restart both services mid-checkout, interrupt
   the API and IPN, rotate credentials with pending invoices, restore a backup,
   try another store's ID and test an invalid signature. Add active CI and an
   independent review of exact-amount accounting and the invoice state machine.
3. **Mobile and upgrade checks.** Test iOS/Android wallet apps, iframe allow/block,
   full-page return, 0.1.0 → 0.2.0 with pending invoices, and every BTCPay version
   claimed compatible. The current automated host is 2.4.4 only.

## Most useful next features

| Priority | Feature | Why |
| --- | --- | --- |
| 1 | Connection health panel | Separate API read/write readiness, accepted methods, last verified IPN and last successful reconciliation. A green read test alone is not end-to-end readiness. |
| 2 | Linked payments / Needs review list | Search BTCPay order and Wholly invoice IDs, see original fiat plus paid chain/token/amount, and retry verification. No manual “paid” shortcuts. |
| 3 | Notification/polling diagnostics | Show last API check, next retry and actionable errors without API keys or customer payloads. Helps resolve outages without server logs. |
| 4 | Store-level payment selection | Optionally limit the Wholly methods offered through BTCPay to selected store-accepted chains/assets, e.g. stablecoins only. Never broaden Wholly store policy. |
| 5 | Safe refund handoff | Open the matched Wholly payment for an explicit merchant review. A browser action must not silently send funds or guess a return address. |

After the release gates, submit a tested build to the official
[BTCPay Plugin Builder](https://docs.btcpayserver.org/Development/Plugins/#publishing-the-plugin)
for installation/update discovery, with documentation, logo and a short demo.
Listing and a successful package build are not security certification.

Keep wallet keys, gas/sweeps, rates, chain scanners and asset management in Wholly.
Duplicating them inside BTCPay would add complexity without helping this connector.
