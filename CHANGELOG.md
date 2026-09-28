# Changelog

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
