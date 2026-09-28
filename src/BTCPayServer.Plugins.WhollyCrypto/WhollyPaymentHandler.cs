using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class WhollyPaymentHandler(Connections connections, CurrencyNameTable currencies) : IPaymentMethodHandler
{
    public static readonly PaymentMethodId Method = new("WHOLLY-CRYPTO");
    public PaymentMethodId PaymentMethodId => Method;
    public JsonSerializer Serializer { get; } = JsonSerializer.CreateDefault();

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = context.InvoiceEntity.Currency;
        context.Prompt.PaymentMethodFee = 0;
        var exact = Protocol.Format(context.InvoiceEntity.Price);
        var dot = exact.IndexOf('.');
        var neededPlaces = dot < 0 ? 0 : exact.Length - dot - 1;
        context.Prompt.Divisibility = Math.Max(currencies.GetCurrencyData(context.InvoiceEntity.Currency, false)?.Divisibility ?? 2, neededPlaces);
        // The prompt is local; the actual remote invoice is still created on click.
        context.Prompt.Inactive = false;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var invoice = context.InvoiceEntity;
        if (invoice.Price <= 0 || invoice.Type != InvoiceType.Standard
            || !System.Text.RegularExpressions.Regex.IsMatch(invoice.Currency, "^[A-Z]{3}$")
            || currencies.GetCurrencyData(invoice.Currency, false) is not { Crypto: false }
            || decimal.Round(invoice.Price, 8) != invoice.Price
            || invoice.ExpirationTime < DateTimeOffset.UtcNow.AddMinutes(5)
            || invoice.ExpirationTime > DateTimeOffset.UtcNow.AddHours(24))
            throw new PaymentMethodUnavailableException("Wholly Crypto needs a positive, fixed fiat invoice (at most 8 decimal places) with 5 minutes to 24 hours remaining.");
        try
        {
            var config = (MethodConfig)ParsePaymentMethodConfig(context.PaymentMethodConfig);
            await connections.Get(invoice.StoreId, config.ConnectionId);
            var root = new Uri(invoice.ServerUrl, UriKind.Absolute);
            if (root.Scheme != "https" || root.UserInfo.Length != 0)
                throw new ConnectorException("BTCPay must have a public HTTPS URL for Wholly callbacks.");
            context.Prompt.Destination = new Uri(root, "plugins/whollycrypto/pay/" + Uri.EscapeDataString(invoice.Id)).AbsoluteUri;
            context.Prompt.Details = JObject.FromObject(new PromptDetails
            {
                ConnectionId = config.ConnectionId, Amount = invoice.Price, Currency = invoice.Currency,
                RequestId = "btcpay-" + invoice.Id + "-" + Guid.NewGuid().ToString("N")
            });
        }
        catch (Exception e) { throw new PaymentMethodUnavailableException(Protocol.SafeError(e)); }
    }

    public object ParsePaymentPromptDetails(JToken details) => details.ToObject<PromptDetails>()!;
    public object ParsePaymentMethodConfig(JToken config) => config.ToObject<MethodConfig>()!;
    public object ParsePaymentDetails(JToken details) => details.ToObject<PaymentDetails>()!;
    public void StripDetailsForNonOwner(object details)
    {
        if (details is PromptDetails p)
        {
            p.ConnectionId = ""; p.RequestId = ""; p.RequestJson = null;
            p.Error = null; p.Review = null;
            p.CallbackInvoiceId = null; p.CallbackEvents = []; p.LastCallbackType = null;
            p.LastCallback = null; p.CallbackVerifiedAt = null; p.CallbackPending = false;
            p.LastAttempt = null; p.CreatedViaApiAt = null; p.LastCheck = null; p.NextCheck = null;
        }
    }

    public async Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext ctx)
    {
        try { await connections.Get(ctx.Store.Id, ((MethodConfig)ParsePaymentMethodConfig(ctx.Config)).ConnectionId); }
        catch { ctx.ModelState.AddModelError("ConnectionId", "Choose a connection saved under this BTCPay store's Wholly Crypto settings."); }
    }
}
