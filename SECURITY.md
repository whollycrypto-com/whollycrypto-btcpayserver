# Security

This is a preview connector. Test with a staging deployment before accepting
customer payments. Keep BTCPay, .NET, PostgreSQL and your Wholly merchant updated.

Report vulnerabilities privately through [Wholly Crypto contact](https://www.whollycrypto.com/contact/).
Do not include credentials, signed live callback bodies, customer details, private
keys or recovery phrases in a public issue. Describe the impact and a synthetic
reproducer; arrange sensitive disclosure privately.

Use a dedicated store and project-restricted API key. Save the store IPN secret,
not an unrelated webhook secret. API keys authorize invoice access/creation but
the connector has no reason to receive wallet secrets. Server administrators can
access the host and its data-protection keys; encryption is not isolation from
the server operator. Back up both BTCPay data and data-protection keys securely.

Browser redirects, callback event names and unverified JSON are never payment
proof. Failed API verification leaves the order unpaid or requiring review.
Review paid-state reversals and double payments manually before any refund.

The package audit excludes symbols, secrets and upstream runtime binaries. Build
dependencies, including the pinned BTCPay source, must also be reviewed; see
the known upstream advisory in [testing](docs/testing.md).
