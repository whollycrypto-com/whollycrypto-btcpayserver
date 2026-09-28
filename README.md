# Wholly Crypto for BTCPay Server

Add stablecoins and other supported networks to a BTCPay checkout through your
own [Wholly Crypto](https://www.whollycrypto.com/) installation. Bitcoin and
Lightning already configured in BTCPay stay unchanged.

**1.0.1** requires BTCPay Server **2.4.4 or newer**, with no maximum version
declared. It is built and tested against **2.4.4** (.NET 10); newer versions still
need deployment testing. This is an independent connector, not an official BTCPay plugin
directory listing or an endorsement by BTCPay Server.

## How it works

1. Your customer chooses **Stablecoins & crypto · Wholly** on a BTCPay invoice.
2. The connector creates one linked Wholly invoice and opens your hosted checkout.
3. Wholly receives and monitors payment using your configured wallets and nodes.
4. Signed IPN notifications queue an authenticated API check. BTCPay records
   the external payment in the original invoice currency after verification.

The connector never receives recovery phrases or private keys. It does not invent
a Bitcoin transaction, hold funds, trade assets or automatically refund payments.
Wholly's processing fees and prepaid-credit rules apply only to payments routed
through Wholly; this connector adds no separate percentage fee.

## Install

Download `BTCPayServer.Plugins.WhollyCrypto.btcpay` and `SHA256SUMS` from
[Releases](https://github.com/whollycrypto-com/whollycrypto-btcpayserver/releases).
Verify the checksum before installing. Do not upload GitHub's source ZIP as a plugin.

In **BTCPay Server 2.4.4**, open **Manage Plugins → Upload Plugin**, upload
the `.btcpay` file and restart BTCPay when prompted. Server-admin access is required.
Test the complete workflow on a staging store before enabling live orders.
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

Allow TCP 443 and POST requests to that route **from the Wholly server**, including
in your hosting provider's firewall. A reachable browser does not prove that the
Wholly server is allowed through. Do not put a browser
login, CAPTCHA or Basic Auth challenge in front of callbacks. Do not disable
signature verification. Keep both servers' clocks synchronized.

## Connection health and linked payments

**Connection** separates read access, successful invoice creation, the last signed
IPN receipt and the last successful API verification. These are observations with
timestamps, not promises that a future payment will work. A fresh connection has
no write/IPN evidence until an actual staging invoice goes through it.

**Linked payments** searches BTCPay invoice IDs, merchant order IDs and Wholly
invoice IDs. Filter pending, settled, needs review or verification errors; results
are paginated. Each row includes original fiat, received asset when reported,
callback state and safe errors. **Check payment** verifies against the API. It
does not resend IPN, clear a review flag, manually mark paid or send funds.

## Choose networks and assets

By default, checkout uses every method accepted by the dedicated Wholly store.
Enable **Choose specific store-accepted methods**, use **Test read access** to
refresh the catalogue, search and select your choices, then save. For example,
offer only Ethereum USDC and USDT through BTCPay. Contracts use exact asset IDs,
not ambiguous ticker matching. Lightning is a separate method.

The plugin cannot enable assets in Wholly. It rechecks the saved subset before
creating a new linked invoice and verifies the returned methods before showing
checkout. If a store-policy change causes the API to fall back to broader defaults,
checkout is blocked with a diagnostic, not silently broadened. Existing invoices
and retries keep their original connection and exact request. Changing selection
only affects new BTCPay invoices. Wallet, pricing and confirmations stay in Wholly.

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
fulfillment and refunds/refund handoff are outside this connector's scope.
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
payment** button; this is not a continuous audit of all historic payments.

Valid IPN is verified and saved locally before a success response. A background
worker then reads authoritative API state; it never accepts a callback's payment
status as proof. Queued work survives restarts and includes expired/settled invoices.
API failures keep it queued with backoff. Repeated events cannot create a second
payment. This avoids holding a delivery open during a slow API call. An early
signed callback can also recover a lost invoice-creation response through a GET.

**Delivery failed in Wholly?** Open Store → IPN → History → Details:

- **Connection timeout:** Wholly cannot reach BTCPay, or the response is too slow.
  Check DNS, the HTTPS listener, both firewalls and the reverse proxy first.
- **401:** compare the store IPN secret (not a webhook secret) and server clocks.
- **403 / login page:** check proxy, WAF, IP restrictions and authentication rules.
- **503:** verification could not be queued. Pending deliveries retry; inspect
  the plugin's linked-payment diagnostics and BTCPay service health.

From your Wholly host, test network reachability without sending an event:

```sh
curl --connect-timeout 5 --max-time 10 https://btcpay.example.com/plugins/whollycrypto/health
```

It should return connector JSON; this does **not** test a signing secret. After
fixing the network/configuration, pending retries continue automatically. For a
permanently failed delivery, resend from Wholly's IPN history. The plugin's Check
payment is a separate API lookup, not a delivery retry. Expired with no received
funds is not a lost payment. Do not ask a customer to pay twice because of an IPN error.

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
signed callbacks, confirmation, API downtime/retry, service restart and exception
review. Automated tests use synthetic Wholly responses and disposable PostgreSQL,
not a live Wholly installation or real funds. A first real staging workflow is
still required. Never post credentials, wallet backups or customer data in issues.

[Architecture](docs/architecture.md) · [Build and test](docs/testing.md) ·
[Deployment readiness](docs/production-readiness.md) ·
[Security](SECURITY.md) · [Wholly API docs](https://www.whollycrypto.com/api/)

MIT licensed. BTCPay Server is a separate project under its own license.
