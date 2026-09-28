# Wholly Crypto for BTCPay Server

Add stablecoins and other supported networks to a BTCPay checkout through your
own [Wholly Crypto](https://www.whollycrypto.com/) installation. Bitcoin and
Lightning already configured in BTCPay stay unchanged.

**0.2.0 is a preview for staging tests**, built against BTCPay Server **2.4.4**
(.NET 10). The declared compatibility range is 2.4.4–2.4.x; other versions have
not been tested. This is an independent connector, not an official BTCPay plugin
directory listing or an endorsement by BTCPay Server.

## How it works

1. Your customer chooses **Stablecoins & crypto · Wholly** on a BTCPay invoice.
2. The connector creates one linked Wholly invoice and opens your hosted checkout.
3. Wholly receives and monitors payment using your configured wallets and nodes.
4. Signed IPN notifications trigger an authenticated API check. BTCPay records
   the external payment in the original invoice currency after verification.

The connector never receives recovery phrases or private keys. It does not invent
a Bitcoin transaction, hold funds, trade assets or automatically refund payments.
Wholly's processing fees and prepaid-credit rules apply only to payments routed
through Wholly; this connector adds no separate percentage fee.

## Install

Download `BTCPayServer.Plugins.WhollyCrypto.btcpay` and `SHA256SUMS` from
[Releases](https://github.com/whollycrypto-com/whollycrypto-btcpayserver/releases).
Verify the checksum before installing. Do not upload GitHub's source ZIP as a plugin.

In a **test BTCPay Server 2.4.4**, open **Manage Plugins → Upload Plugin**, upload
the `.btcpay` file and restart BTCPay when prompted. Server-admin access is required.
See [BTCPay's plugin documentation](https://docs.btcpayserver.org/Development/Plugins/).

Updates currently use the same manual upload process. A merchant-software update
does not update this plugin. No automatic plugin-directory updates are promised.

## Setup

In Wholly Crypto:

- Use a current merchant installation with the public `invoice_id` API contract.
  This version targets the 6.4.5 contract.
- Create a dedicated, enabled project/store and configure accepted payment methods,
  backed-up wallets, rates and confirmation requirements. Keep invoice currency enabled.
- Create a read/write API credential restricted to that project. Do not use a
  global credential. An API IP allowlist can restrict it to the BTCPay server.
- Enable Store → IPN and copy its **IPN signing secret**, not a webhook secret.
- Copy the Project and Store **API IDs** from Store → Basic → API IDs.

In BTCPay, **select a store → Plugins → Wholly Crypto** in the left menu.
The same page is also available under store settings. Installed Plugins → Wholly
Crypto → **Details** opens this guide. Installing alone does not connect a store.
Enter:

| Setting | Example |
| --- | --- |
| Wholly API URL | `https://api.example.com` |
| Wholly checkout URL | `https://pay.example.com` |
| Project / Store API ID | The two UUIDs copied above |
| API credential | The project-restricted read/write credential |
| Store IPN signing secret | The dedicated store's IPN secret |

Enable **Offer Wholly Crypto at checkout**, **Save connection first**, then use
**Test read access**. The read test does
not prove write permissions or IPN delivery: complete the staging checklist below.
Secret inputs stay blank when editing; leave them blank to keep saved values.

Cannot see the menu? Restart BTCPay after uploading, select a store, and use an
account with permission to modify its settings. The direct route is
`/stores/<BTCPAY_STORE_ID>/whollycrypto` on your BTCPay server. This is the BTCPay
store ID from its URL, not the Wholly Store API ID.

Both servers need public HTTPS. This first version accepts Wholly origins on port
443 only, with no URL path, proxy credentials, localhost or private-network address.
Set the checkout URL to the store's default checkout domain. BTCPay must advertise
its real external HTTPS URL. Reverse proxies must preserve the correct host/scheme.

The plugin supplies this per-invoice callback automatically:

```text
https://btcpay.example.com/plugins/whollycrypto/callback/<BTCPAY_INVOICE_ID>
```

Allow POST requests to that route from the Wholly server. Do not put a browser
login, CAPTCHA or Basic Auth challenge in front of callbacks. Do not disable
signature verification. Keep both servers' clocks synchronized.

## Embedded checkout (optional)

Full-page checkout remains the default and works best with wallet-app links.
To show Wholly inside an iframe on a BTCPay-hosted payment page:

1. In **Wholly → Project → Stores → your store → Advanced**, enable **Allow
   embedded checkout**. Add `https://btcpay.example.com` to **Allowed HTTPS origins**,
   replacing it with your actual BTCPay origin. No path, wildcard or API credential.
2. In **BTCPay → Plugins → Wholly Crypto → Customer checkout**, choose **Embedded
   checkout (iframe)** and save.
3. Create a **new** BTCPay invoice and test it. Existing invoices retain the
   connection and display mode they were created with.

The customer still chooses Wholly explicitly. The connector then opens its embedded
payment page with the same hosted invoice. **Open full checkout** stays visible
for blocked frames, mobile wallet apps and accessibility. It opens the same invoice,
not another payment request. Do not pay twice.

The Wholly checkout origin must exactly match the configured checkout URL. If a
proxy adds `X-Frame-Options: DENY` or a conflicting `frame-ancestors` policy, the
frame will not load; keep the fallback or correct the checkout-specific policy.
Never remove frame protection globally. A successful read-access test does not
verify frame permissions, invoice creation, wallet deep links or IPN delivery.

Embedded mode disables Wholly's automatic in-frame redirect. BTCPay's local,
server-verified state controls the automatic return. Explicit return links only
navigate back; browser messages never settle invoices or count as payment proof.

## Payment rules

| Verified Wholly state | Connector / BTCPay behavior |
| --- | --- |
| `new`, no funds | No payment recorded |
| `processing`, partial amount | Visible in Wholly details; no full-value payment recorded |
| `processing`, paid/overpaid | Original fiat amount recorded as processing |
| `settled`, paid/overpaid, automatic/on-time | External payment settles; BTCPay's invoice watcher completes an eligible invoice |
| Late, manually resolved, invalid, competing BTCPay payment | Review required; no automatic fulfillment from this connector |
| Previously settled payment becomes invalid | Payment becomes unaccounted and review is flagged; shipped goods cannot be undone |

**Fulfill from BTCPay's settled invoice, once per order**, not the browser return
or an IPN event name alone. Different event types can share the same Wholly status.
The plugin validates the current API state and deduplicates the payment record.

Fixed, positive fiat invoices only, with at most eight decimal places. At least
five minutes and no more than 24 hours must remain when the payment method is
initialized. Crypto-denominated invoices, top-up invoices, mixed/partial-method
fulfillment and automatic refunds are not supported in this first version.
An overpayment settles the original fiat invoice, not additional store credit;
inspect the actual crypto amounts in Wholly before refunding any excess.

Do not pay through both BTCPay Bitcoin/Lightning and Wholly. The connector checks
for competing payments, but it cannot stop a customer sending funds to two already
issued payment requests. A delayed second payment always needs merchant review.

## Recovery and operations

Creation is lazy and idempotent. The exact request and retry key are saved before
calling Wholly. After a timeout, use the same BTCPay invoice; do not start a second
order. If its creation outcome is still unknown after BTCPay expires, search Wholly
for the order reference `btcpay:<BTCPAY_INVOICE_ID>` and reconcile manually.

Pending invoices also receive periodic API checks (normally about once a minute;
backlogs and API rate limits can delay this). This supports missed IPN delivery.
After final settlement, reorg detection depends on IPN or the invoice's **Check
payment** button; this preview is not a continuous audit of all historic payments.

Low Wholly credits may pause IPN. API polling remains subject to the merchant API's
access and rate-limit policies; it is not a substitute for healthy credits and nodes.
No response, timeout or browser redirect counts as payment proof.

Connection secrets use BTCPay's ASP.NET Data Protection. Back up its database **and
data-protection keys** together. Connections are versioned so existing invoices
retain their original association. Do not revoke old API credentials or rotate
store IPN secrets until their pending invoices have been reconciled. Disabling the
method stops new checkouts; already-created Wholly invoices remain payable until
they expire. The plugin does not remotely cancel them.

Review warnings appear in BTCPay invoice details. Fix the cause and reconcile both
systems before using BTCPay's administrative status controls. Review flags are
deliberately not auto-cleared. Refunds must be handled separately after checking
the destination, amount and original payment.

## Before live use

Run the [staging checklist](docs/testing.md) on your actual deployment: checkout,
signed callbacks, confirmation, API downtime/retry, service restart and refund
review. Automated tests use synthetic Wholly responses and disposable PostgreSQL,
not a live Wholly installation or real funds. A first real staging workflow is
still required. Never post credentials, wallet backups or customer data in issues.

[Architecture](docs/architecture.md) · [Build and test](docs/testing.md) ·
[1.0 readiness plan](docs/production-readiness.md) ·
[Security](SECURITY.md) · [Wholly API docs](https://www.whollycrypto.com/api/)

MIT licensed. BTCPay Server is a separate project under its own license.
