# Changelog

## 1.0.1 - 2026-09-28

- Fix Plugin Builder manifest validation with the minimum-only BTCPay Server
  dependency `>=2.4.4`, without a maximum version.
- Add compiled-metadata and generated-manifest regression checks, plus exact
  Plugin Builder submission fields. Build/test pin remains BTCPay 2.4.4.
- No payment, accounting, callback or checkout behavior changes. Version 1.0.0
  and its published assets remain unchanged; submit the new `v1.0.1` tag.

## 1.0.0 - 2026-09-28

- Connection health separates read access, observed invoice creation, signed IPN
  receipt and API reconciliation, with timestamps and actionable network guidance.
- Searchable linked-payments list with 20-row pagination, original fiat, received
  asset, review/error filters and safe API verification retries.
- Store-level accepted network/asset selection, exact asset IDs and checkout
  safeguards against policy changes or the API's unmatched-selection fallback.
- Valid callbacks are acknowledged after durable local queueing instead of waiting
  for a remote API request. Verification survives restarts, respects backoff and
  processes terminal invoices; early IPN can recover a lost creation response.
- More precise safe HTTP errors, callback/API timestamps and duplicate handling.
- Keeps embedded/full-page checkout, immutable old connections and original-fiat
  accounting. No refund handoff or refund execution included.

Built/tested against pinned BTCPay 2.4.4 using synthetic API/payment fixtures and
disposable PostgreSQL. Complete the documented staging checklist on your own
deployment; no real-money end-to-end certification or directory listing is claimed.

## 0.2.0 - 2026-09-28

Preview update for BTCPay Server 2.4.4.

- Discoverable under Plugins after selecting a store, plus existing settings link.
- Installed-plugin documentation resource and concise setup instructions.
- Optional iframe checkout on a BTCPay-hosted page; full-page mode stays default.
- Exact-origin embedding instructions, persistent full-page fallback and safe return.
- Official bundled logo on settings, checkout and customer payment pages.
- Tests and a practical 1.0 release-readiness plan; not a production certification.

Existing connections/invoices keep their original display mode and payment rules.
Real staging payments and mobile wallet-app testing remain required.

## 0.1.0 - 2026-09-28

First preview connector for BTCPay Server 2.4.4.

- Store-scoped, encrypted Wholly API/IPN connection settings.
- Separate stablecoin/crypto checkout option; existing Bitcoin/Lightning unchanged.
- Durable, lazy invoice creation with exact-body idempotent retries.
- Signed notifications plus authoritative invoice re-fetch and bounded polling.
- Original-fiat external-payment accounting with duplicate protection.
- Explicit review for late/manual/reorg and competing-method payments.
- Compiled plugin package, reproducible build instructions and synthetic tests.

Not yet verified with a live Wholly payment or listed in BTCPay's plugin directory.
