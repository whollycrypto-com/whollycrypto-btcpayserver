# Wholly Crypto BTCPay connector

Public integration code only. Never add wallet keys, runtime state, credentials,
or implementation code from the separate payment processor. Read README.md,
docs/architecture.md and docs/testing.md before changes.

Use only the public merchant API. Verify exact decimal amounts, invoice/store/
project identity, callbacks, idempotency and settlement before payment accounting.
Browser redirects are not payment proof. Do not fake a Bitcoin transaction.
Keep public customer views free of protected connection data and request bodies.

The BTCPay submodule is pinned. Build and test against that revision before
changing compatibility claims. Package only the connector assemblies/resources.
Publishing source, packaging, Plugin Builder listing and live deployment are
separate outcomes. Tests must use synthetic invoices, never live funds.
