using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class WhollyBridge(InvoiceRepository invoices, StoreRepository stores, Connections connections,
    IWhollyClient client, InvoiceLock locks, WhollyPaymentHandler handler, PaymentService payments, EventAggregator events)
{
    public static PromptDetails? Details(InvoiceEntity invoice) => invoice.GetPaymentPrompt(WhollyPaymentHandler.Method)?.Details?.ToObject<PromptDetails>();

    public static string? CannotStart(InvoiceEntity invoice, PromptDetails p, DateTimeOffset now)
    {
        if (invoice.Archived || invoice.Status != InvoiceStatus.New || invoice.Price != p.Amount || invoice.Currency != p.Currency)
            return "This invoice is no longer available for a new Wholly payment.";
        if (invoice.GetPayments(true).Any(x => x.Value > 0)) return "A payment is already attached to this invoice. Do not pay again.";
        if (invoice.ExpirationTime < now.AddMinutes(5)) return "Fewer than five minutes remain. Ask the merchant for a new invoice.";
        return null;
    }

    public async Task<PromptDetails> Synchronize(string id, bool start, CancellationToken ct)
    {
        await using var lease = await locks.Acquire(id, ct);
        var invoice = await invoices.GetInvoice(id) ?? throw new ConnectorException("Invoice not found.");
        var p = Details(invoice) ?? throw new ConnectorException("Wholly Crypto is not enabled for this invoice.");
        try
        {
            var c = await connections.Get(invoice.StoreId, p.ConnectionId);
            if (p.RequestJson is null)
            {
                if (!start) return p;
                var reason = CannotStart(invoice, p, DateTimeOffset.UtcNow);
                if (reason is not null) throw new ConnectorException(reason);
                var store = await stores.FindStore(invoice.StoreId);
                if (store?.GetPaymentMethodConfig(WhollyPaymentHandler.Method, true) is null)
                    throw new ConnectorException("The merchant has disabled new Wholly payments.");
                var baseUrl = new Uri(invoice.ServerUrl, UriKind.Absolute);
                var returnUrl = new Uri(baseUrl, (c.EmbedCheckout ? "plugins/whollycrypto/return/" : "i/") + Uri.EscapeDataString(id)).AbsoluteUri;
                JArray? selection = null;
                if (c.AcceptedMethods is not null)
                {
                    var catalog = PaymentSelection.Catalog(await client.Request(c,
                        $"/v1/projects/{c.ProjectId}/stores/{c.StoreId}/payment-assets", null, null, ct));
                    selection = PaymentSelection.Request(PaymentSelection.Select(c.AcceptedMethods.Select(x => x.Key), catalog));
                }
                var request = new JObject
                {
                    ["amount"] = Protocol.Format(p.Amount), ["currency"] = p.Currency, ["order_id"] = "btcpay:" + id,
                    ["description"] = "Payment for BTCPay invoice " + id,
                    ["expires_in_seconds"] = Math.Clamp((int)(invoice.ExpirationTime - DateTimeOffset.UtcNow).TotalSeconds, 300, 86400),
                    ["ipn_url"] = new Uri(baseUrl, "plugins/whollycrypto/callback/" + Uri.EscapeDataString(id)).AbsoluteUri,
                    ["redirect_url"] = returnUrl, ["cancel_url"] = returnUrl, ["redirect_automatically"] = !c.EmbedCheckout,
                    ["metadata"] = new JObject { ["btcpay_invoice_id"] = id, ["btcpay_store_id"] = invoice.StoreId,
                        ["order_id"] = invoice.Metadata.OrderId }
                };
                if (selection is not null) request["payment_methods"] = selection;
                p.RequestJson = request.ToString(Formatting.None);
                await Save(id, p); // The original bytes survive a timeout or restart.
            }
            var lookupId = p.InvoiceId ?? p.CallbackInvoiceId;
            if (lookupId is null && invoice.ExpirationTime <= DateTimeOffset.UtcNow)
                throw new ConnectorException("Creation outcome is unknown and the BTCPay invoice expired. Reconcile the Wholly order reference manually; do not create another payment.");
            var path = "/v1/projects/" + c.ProjectId + (lookupId is null
                ? "/stores/" + c.StoreId + "/invoices" : "/invoices/" + lookupId);
            p.LastAttempt = DateTimeOffset.UtcNow;
            var response = await client.Request(c, path, lookupId is null ? p.RequestJson : null, p.RequestId, ct);
            var remote = response["data"] as JObject ?? throw new ConnectorException("Invalid Wholly invoice response.");
            Protocol.Match(p, c, id, remote);
            var verifiedId = Protocol.Uuid(Protocol.Required(remote, "invoice_id"));
            if (p.CallbackInvoiceId is not null && p.CallbackInvoiceId != verifiedId)
                throw new ConnectorException("Callback invoice does not match the API response. Review this payment.");
            p.InvoiceId = verifiedId;
            if (lookupId is null) p.CreatedViaApiAt = DateTimeOffset.UtcNow;
            PaymentSelection.VerifyInvoice(c, remote);
            p.CheckoutUrl = Protocol.Checkout(c, p.InvoiceId, (string?)response["links"]?["checkout"] ?? "");
            p.RemoteStatus = Protocol.Required(remote, "status");
            p.AmountStatus = Protocol.Required(remote, "amount_status");
            p.Sequence = remote["sequence"]!.Value<long>();
            p.LastCheck = DateTimeOffset.UtcNow; p.NextCheck = p.LastCheck.Value.AddSeconds(60); p.Error = null;
            (p.PaidChain, p.PaidAsset, p.PaidAmount) = ObservedAsset(remote);
            // Reload after network I/O: Bitcoin may have arrived while Wholly was being checked.
            invoice = await invoices.GetInvoice(id);
            await Apply(invoice, p, remote);
            if (p.CallbackPending) { p.CallbackVerifiedAt = p.LastCheck; p.CallbackPending = false; }
            await Save(id, p);
            return p;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            p.Error = Protocol.SafeError(e);
            p.NextCheck = DateTimeOffset.UtcNow.AddSeconds(e is ConnectorException ce ? ce.RetryAfterSeconds : 60);
            await Save(id, p);
            throw new ConnectorException(p.Error, e is ConnectorException ce2 ? ce2.RetryAfterSeconds : 60);
        }
    }

    public static string? ReviewReason(InvoiceEntity invoice, PromptDetails p, JObject remote)
    {
        if (p.AmountStatus == "none" && p.RemoteStatus != "settled") return null;
        if (invoice.Price != p.Amount || invoice.Currency != p.Currency) return "BTCPay amount or currency changed.";
        if (invoice.Archived || invoice.Status == InvoiceStatus.Invalid) return "The BTCPay invoice was archived or invalidated.";
        if (invoice.GetPayments(true).Any(x => x.PaymentMethodId != WhollyPaymentHandler.Method && x.Value > 0))
            return "A different BTCPay method also received payment. Review both payments; do not fulfil twice.";
        if ((string?)remote["timing_status"] == "late") return "Wholly reported a late payment.";
        if ((string?)remote["resolution"] != "automatic") return "The Wholly invoice was manually resolved. Review it before fulfilling the BTCPay order.";
        if (p.RemoteStatus is "invalid" or "cancelled") return "Wholly no longer considers this payment valid.";
        return null;
    }

    public static (string? Chain, string? Asset, string? Amount) ObservedAsset(JObject remote)
    {
        // paid_chain/paid_asset belong to notification payloads, not the invoice
        // GET contract. Derive display data from API-verified payment intents.
        if (remote["payment_intents"] is not JArray intents) return (null, null, null);
        var funded = intents.OfType<JObject>().Where(x => x["received_amount"]?.Type == JTokenType.String
            && Protocol.Decimal((string)x["received_amount"]!) != "0").ToArray();
        var winner = (string?)remote["winning_payment_intent_id"];
        var method = winner is not null ? funded.SingleOrDefault(x => (string?)x["id"] == winner)
            : funded.Length == 1 ? funded[0] : null;
        return method is null ? (null, null, null) : (Protocol.Required(method, "chain_slug"),
            Protocol.Required(method, "symbol"), Protocol.Decimal(Protocol.Required(method, "received_amount")));
    }

    private async Task Apply(InvoiceEntity invoice, PromptDetails p, JObject remote)
    {
        var old = invoice.GetPayments(false).SingleOrDefault(x => x.PaymentMethodId == WhollyPaymentHandler.Method);
        var reason = ReviewReason(invoice, p, remote);
        if (old?.Status == PaymentStatus.Settled && p.RemoteStatus != "settled") reason = "A previously settled Wholly payment changed status. Review fulfilment and possible reorg.";
        if (reason is not null && p.Review != reason)
        {
            p.Review = reason;
            await invoices.AddInvoiceEvent(invoice.Id, "Wholly Crypto: " + reason, InvoiceEventData.EventSeverity.Error);
        }
        var fullyReceived = p.AmountStatus is "paid" or "overpaid";
        var status = p.Review is not null || !fullyReceived || p.RemoteStatus is not ("processing" or "settled")
            ? PaymentStatus.Unaccounted : p.RemoteStatus == "settled" ? PaymentStatus.Settled : PaymentStatus.Processing;
        var details = new PaymentDetails { InvoiceId = p.InvoiceId!, Chain = p.PaidChain,
            Asset = p.PaidAsset, AssetAmount = p.PaidAmount,
            Resolution = (string?)remote["resolution"] };
        if (old is not null)
        {
            var changed = old.Status != status;
            old.Status = status; old.Details = JObject.FromObject(details);
            await payments.UpdatePayments([old]);
            if (!changed) return;
        }
        else if (fullyReceived && p.RemoteStatus is "processing" or "settled")
        {
            // Accounting unit is original invoice fiat, NOT a fabricated BTC transfer.
            var payment = new PaymentData { Id = "wholly:" + p.InvoiceId, Created = DateTimeOffset.UtcNow,
                Currency = p.Currency, Amount = p.Amount, Status = status }.Set(invoice, handler, details);
            await payments.AddPayment(payment, [p.InvoiceId!]);
        }
        else return;
        var updated = await invoices.GetInvoice(invoice.Id);
        events.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment));
    }

    private Task Save(string id, PromptDetails p) => invoices.UpdatePaymentDetails(id, handler, p);

    public async Task Callback(string id, byte[] raw, string signature, string eventId, CancellationToken ct)
    {
        var invoice = await invoices.GetInvoice(id) ?? throw new ConnectorException("Unknown callback invoice.");
        var p = Details(invoice) ?? throw new ConnectorException("Unknown callback method.");
        var c = await connections.Get(invoice.StoreId, p.ConnectionId);
        if (!Protocol.VerifySignature(raw, signature, c.IpnSecret, DateTimeOffset.UtcNow))
            throw new UnauthorizedAccessException();
        var payload = Protocol.Json(raw);
        if (Protocol.Uuid(Protocol.Required(payload, "event_id")) != Protocol.Uuid(eventId)) throw new UnauthorizedAccessException();
        // Do not await the remote API in a delivery request. Wholly's delivery
        // timeout is shorter than an API check, and its creation event may arrive
        // before the original creation response. Save a durable verification job.
        await using var lease = await locks.Acquire(id, ct, attempts: 5);
        invoice = await invoices.GetInvoice(id) ?? throw new ConnectorException("Unknown callback invoice.");
        p = Details(invoice) ?? throw new ConnectorException("Unknown callback method.");
        if (p.RequestJson is null) throw new UnauthorizedAccessException();
        // A retried old event may have a lower sequence: use it only as an authenticated
        // notification to fetch current state, never as authority for settlement.
        var check = new PromptDetails { InvoiceId = p.InvoiceId ?? p.CallbackInvoiceId, Amount = p.Amount, Currency = p.Currency };
        Protocol.Match(check, c, id, payload);
        var callbackId = Protocol.Uuid(eventId);
        if (p.CallbackEvents.Contains(callbackId)) return;
        p.CallbackInvoiceId = Protocol.Uuid(Protocol.Required(payload, "invoice_id"));
        p.CallbackPending = true; p.LastCallback = DateTimeOffset.UtcNow;
        var type = (string?)payload["event_type"];
        p.LastCallbackType = type is not null && System.Text.RegularExpressions.Regex.IsMatch(type, @"\A[a-z._]{1,64}\z") ? type : "notification";
        p.CallbackEvents = p.CallbackEvents.TakeLast(15).Append(callbackId).ToList();
        // Preserve API Retry-After on an outage. A new event is not a bypass.
        if (p.Error is null || p.NextCheck is null) p.NextCheck = DateTimeOffset.UtcNow;
        await Save(id, p);
    }
}
