# Changelog

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
