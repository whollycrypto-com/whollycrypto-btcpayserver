# Connector architecture

This repository contains only a BTCPay connector. The separate processor is reached
over its public merchant API. No processor implementation is bundled.

## Source map

| Concern | File |
| --- | --- |
| Registration and version compatibility | `Plugin.cs`, project file |
| Fiat payment method and customer presentation | `WhollyPaymentHandler.cs`, `WhollyCheckout.cs`, `Views/` |
| Credential protection and connection versions | `Connections.cs` |
| Durable request, reconciliation and accounting | `WhollyBridge.cs`, `InvoiceLock.cs` |
| HTTPS client, SSRF boundaries, timeout and backoff | `WhollyClient.cs` |
| Exact amounts, identity, signature and JSON validation | `Protocol.cs` |
| Settings, customer POST and IPN routes | `WhollyCryptoController.cs` |
| Pending-invoice recovery | `WhollyWorker.cs` |
| Offline/manual-upload documentation card | `PluginResourcesFilter.cs` |
| Embedded checkout / return bridge | `Views/WhollyCrypto/Embedded.cshtml`, `Return.cshtml`, `Resources/js/` |

All source files above are under `src/BTCPayServer.Plugins.WhollyCrypto/`.

## Persistence

No extra tables or migrations. Versioned, encrypted connections use BTCPay store
settings. Invoice prompt details hold connection ID, exact saved request, retry
key, external invoice ID, polling state and review reason. BTCPay's payment table
holds one `WHOLLY-CRYPTO` payment with ID `wholly:<invoice_id>`.

PostgreSQL advisory locks serialize work per BTCPay invoice, including across
processes. Locks are session-scoped, bounded while acquiring, and released on
connection disposal. The lock is not a transaction over a remote HTTP request.
BTCPay payment uniqueness provides a second duplicate barrier.

Amounts stay decimal strings on the wire and .NET decimal locally. Payment
accounting uses the original fiat currency, with an equal-currency rate of one.
Actual crypto amounts and tx IDs stay authoritative in Wholly; no fake chain event
is created. BTCPay's own InvoiceWatcher makes the final invoice transitions.

## Trust boundaries

Store settings require BTCPay's store-settings permission and CSRF protection.
Customer payment POST also needs CSRF. Public IPN POST instead requires a bounded,
unambiguous body and a timestamped HMAC over its exact bytes, with a five-minute
clock window. A valid IPN only triggers a fresh API lookup; it never directly
settles an invoice. Project, store, invoice ID, order ID, amount, currency and
sequence are all checked against the saved association.

Outbound HTTP disables redirects and proxies, rejects private/reserved DNS
results, connects to the validated IP and retains ordinary TLS validation. API
credentials are not sent to the checkout domain. Redirect destinations must match
the configured checkout origin and exact invoice path. Network errors never log
API keys, callback bodies or raw remote error text.

Non-owner payment-prompt responses strip connection IDs, request bodies and
operator diagnostics. Neither browser success nor client-supplied status is proof.

Embedded mode is part of the immutable protected connection, defaulting to false
for pre-existing connections. It changes only display/return URLs, not accounting.
The customer POST creates/reuses the same invoice, then uses PRG to a BTCPay-hosted
wrapper. Its CSP permits frames only from the pinned checkout origin and itself;
the wrapper cannot be framed. Wholly independently enforces the store's allowed
embed origins. The iframe is sandboxed with the capabilities needed for checkout
and user-activated wallet links. No wallet/API credentials enter HTML or messages.

The parent checks only local BTCPay state every ten seconds while visible. These
requests never call the Wholly API or mutate payments. A same-origin return bridge
may request navigation only when both message source and origin match; arbitrary
Wholly/browser messages cannot signal success. The explicit full-page fallback is
always visible because iframe load events cannot detect cross-origin policy blocks.

The plugin resource filter alters only its own installed card view model. It does
not modify upstream files, rely on DOM rewrites or claim directory membership.

## Intentionally limited scope

There is one Wholly connection per BTCPay store, no wallet-key access, no exchange,
no payout/refund endpoint, no token-specific BTCPay on-chain handler and no changes
to Bitcoin/Lightning handlers. Customers choose networks/assets in hosted Wholly.
Fiat precision is capped at eight places and Wholly validates enabled currencies.

Partial/late/mixed payments need review. Polling handles pending invoices, not an
unbounded historical reorg scan. Automatic fulfillment is not reversible after an
external order system has shipped. These boundaries must stay explicit in the UI
and README when functionality changes.
