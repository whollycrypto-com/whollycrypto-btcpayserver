using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using BTCPayServer.Filters;
using BTCPayServer.Security;
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.WhollyCrypto;

[AutoValidateAntiforgeryToken]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class WhollyCryptoController(StoreRepository stores, Connections connections, IWhollyClient client,
    WhollyPaymentHandler handler, InvoiceRepository invoices, WhollyBridge bridge, ActivityRepository activity) : Controller
{
    [HttpGet("~/stores/{storeId}/whollycrypto")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null || store.Id != storeId) return NotFound();
        var config = store.GetPaymentMethodConfig(WhollyPaymentHandler.Method)?.ToObject<MethodConfig>();
        var vm = new SettingsModel { StoreId = storeId };
        if (config is not null)
        {
            var c = await connections.Get(storeId, config.ConnectionId);
            vm.ApiOrigin = c.ApiOrigin; vm.CheckoutOrigin = c.CheckoutOrigin; vm.ProjectId = c.ProjectId;
            vm.WhollyStoreId = c.StoreId; vm.HasSavedConnection = true;
            vm.EmbedCheckout = c.EmbedCheckout;
            vm.LimitMethods = c.AcceptedMethods is not null;
            vm.SelectedMethods = c.AcceptedMethods?.Select(x => x.Key).ToList() ?? [];
            vm.Health = await connections.Health(storeId, config.ConnectionId);
            vm.Activity = await activity.Health(storeId, config.ConnectionId, HttpContext.RequestAborted);
            // Preserve saved selections even when the last catalogue could not load.
            if (vm.Health is null && c.AcceptedMethods is not null)
                vm.Health = new ConnectionHealth { Assets = c.AcceptedMethods };
            vm.Enabled = store.GetPaymentMethodConfig(WhollyPaymentHandler.Method, true) is not null;
        }
        vm.Message = TempData["WhollyMessage"] as string;
        return View("Settings", vm);
    }

    [HttpPost("~/stores/{storeId}/whollycrypto")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Settings(string storeId, SettingsModel vm, string command, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null || store.Id != storeId) return NotFound();
        vm.StoreId = storeId;
        var oldConfig = store.GetPaymentMethodConfig(WhollyPaymentHandler.Method)?.ToObject<MethodConfig>();
        vm.HasSavedConnection = oldConfig is not null;
        var old = oldConfig is null ? null : await connections.Get(storeId, oldConfig.ConnectionId);
        if (oldConfig is not null)
        {
            vm.Health = await connections.Health(storeId, oldConfig.ConnectionId);
            vm.Activity = await activity.Health(storeId, oldConfig.ConnectionId, ct);
            if (vm.Health is null && old?.AcceptedMethods is not null) vm.Health = new ConnectionHealth { Assets = old.AcceptedMethods };
        }
        try
        {
            if (!ModelState.IsValid) return CleanSettings(vm);
            var c = new Connection { ApiOrigin = vm.ApiOrigin, CheckoutOrigin = vm.CheckoutOrigin,
                ProjectId = vm.ProjectId, StoreId = vm.WhollyStoreId, EmbedCheckout = vm.EmbedCheckout,
                ApiKey = string.IsNullOrWhiteSpace(vm.ApiKey) ? old?.ApiKey ?? "" : vm.ApiKey.Trim(),
                IpnSecret = string.IsNullOrWhiteSpace(vm.IpnSecret) ? old?.IpnSecret ?? "" : vm.IpnSecret.Trim() };
            Protocol.ValidateConnection(c);
            if (command == "test")
            {
                var health = new ConnectionHealth { CheckedAt = DateTimeOffset.UtcNow };
                try { health.Assets = PaymentSelection.Catalog(await client.Request(c, $"/v1/projects/{c.ProjectId}/stores/{c.StoreId}/payment-assets", null, null, ct)); }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { health.Error = Protocol.SafeError(e); }
                vm.Health = health;
                if (oldConfig is not null && SameApi(c, old!)) await connections.SaveHealth(storeId, oldConfig.ConnectionId, health);
                vm.Message = health.Error is null
                    ? "Read access works and payment methods were refreshed. Write access and incoming IPN need a real staging invoice. Form changes have not been saved."
                    : "Connection check failed. Saved settings have not changed.";
                return CleanSettings(vm);
            }
            if (command != "save") return BadRequest();
            if (vm.LimitMethods)
            {
                if (!vm.Enabled && old?.AcceptedMethods is not null && SameApi(c, old)
                    && vm.SelectedMethods.Order().SequenceEqual(old.AcceptedMethods.Select(x => x.Key).Order()))
                    c.AcceptedMethods = old.AcceptedMethods; // Pausing must still work during an API outage.
                else
                {
                    var catalog = PaymentSelection.Catalog(await client.Request(c, $"/v1/projects/{c.ProjectId}/stores/{c.StoreId}/payment-assets", null, null, ct));
                    c.AcceptedMethods = PaymentSelection.Select(vm.SelectedMethods, catalog);
                    vm.Health = new ConnectionHealth { CheckedAt = DateTimeOffset.UtcNow, Assets = catalog };
                }
            }
            var id = await connections.Save(storeId, c);
            if (vm.Health is not null && (old is not null && SameApi(c, old) || vm.LimitMethods && vm.Enabled))
                await connections.SaveHealth(storeId, id, vm.Health);
            store.SetPaymentMethodConfig(handler, new MethodConfig { ConnectionId = id });
            var blob = store.GetStoreBlob(); blob.SetExcluded(WhollyPaymentHandler.Method, !vm.Enabled);
            store.SetStoreBlob(blob);
            await stores.UpdateStore(store);
            TempData["WhollyMessage"] = "Connection saved. Existing invoices retain their original connection; disabling prevents new Wholly checkouts.";
            return RedirectToAction(nameof(Settings), new { storeId });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ModelState.AddModelError("", Protocol.SafeError(e));
            return CleanSettings(vm);
        }
    }

    private static bool SameApi(Connection a, Connection b) => a.ApiOrigin == b.ApiOrigin && a.ProjectId == b.ProjectId
        && a.StoreId == b.StoreId && a.ApiKey == b.ApiKey;

    [HttpGet("~/stores/{storeId}/whollycrypto/payments")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Payments(string storeId, string? search, string? filter, int page = 1, CancellationToken ct = default)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null || store.Id != storeId) return NotFound();
        var model = await activity.List(storeId, search, filter, page, ct);
        model.Message = TempData["WhollyMessage"] as string;
        return View("Payments", model);
    }

    // Connectivity only, not payment or authentication readiness. No state/IDs/secrets.
    [AllowAnonymous, HttpGet("~/plugins/whollycrypto/health")]
    public IActionResult Health() => Json(new { service = "Wholly Crypto connector", callback_requires_signature = true });

    private IActionResult CleanSettings(SettingsModel vm)
    {
        vm.ApiKey = null; vm.IpnSecret = null;
        ModelState.Remove(nameof(vm.ApiKey)); ModelState.Remove(nameof(vm.IpnSecret));
        return View("Settings", vm);
    }

    [AllowAnonymous, HttpGet("~/plugins/whollycrypto/pay/{invoiceId}")]
    public async Task<IActionResult> Pay(string invoiceId)
    {
        if (!ValidId(invoiceId)) return NotFound();
        var invoice = await invoices.GetInvoice(invoiceId);
        if (invoice is null || WhollyBridge.Details(invoice) is not { } p) return NotFound();
        var reason = p.RequestJson is null ? WhollyBridge.CannotStart(invoice, p, DateTimeOffset.UtcNow) : null;
        return View("Pay", new PayModel(invoiceId, Protocol.Format(p.Amount), p.Currency, ReturnPath(invoiceId),
            reason is null && p.RemoteStatus is not ("settled" or "cancelled" or "invalid" or "expired"), reason));
    }

    [AllowAnonymous, HttpPost("~/plugins/whollycrypto/pay/{invoiceId}")]
    public async Task<IActionResult> Start(string invoiceId, CancellationToken ct)
    {
        if (!ValidId(invoiceId)) return NotFound();
        try
        {
            var p = await bridge.Synchronize(invoiceId, true, ct);
            if (p.Review is not null || p.RemoteStatus is "settled" or "cancelled" or "invalid" or "expired")
                return LocalRedirect(ReturnPath(invoiceId));
            var invoice = await invoices.GetInvoice(invoiceId);
            var c = await connections.Get(invoice.StoreId, p.ConnectionId);
            if (c.EmbedCheckout) return RedirectToAction(nameof(Embedded), new { invoiceId });
            return Redirect(p.CheckoutUrl!);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Response.StatusCode = 503;
            return View("Pay", new PayModel(invoiceId, "", "", ReturnPath(invoiceId), true, Protocol.SafeError(e)));
        }
    }

    [AllowAnonymous, HttpGet("~/plugins/whollycrypto/pay/{invoiceId}/embedded")]
    public async Task<IActionResult> Embedded(string invoiceId, [FromServices] ContentSecurityPolicies policies)
    {
        if (!ValidId(invoiceId)) return NotFound();
        var invoice = await invoices.GetInvoice(invoiceId);
        if (invoice is null || WhollyBridge.Details(invoice) is not { InvoiceId: not null, CheckoutUrl: not null } p) return NotFound();
        if (ShouldReturn(invoice, p)) return LocalRedirect(ReturnPath(invoiceId));
        var c = await connections.Get(invoice.StoreId, p.ConnectionId);
        var checkout = Protocol.Checkout(c, p.InvoiceId, p.CheckoutUrl);
        if (!c.EmbedCheckout) return Redirect(checkout);
        policies.Add(new ConsentSecurityPolicy("default-src", "'self'"));
        policies.Add(new ConsentSecurityPolicy("frame-src", "'self' " + Protocol.Origin(c.CheckoutOrigin).GetLeftPart(UriPartial.Authority)));
        policies.Add(new ConsentSecurityPolicy("frame-ancestors", "'none'"));
        policies.Add(new ConsentSecurityPolicy("object-src", "'none'"));
        policies.Add(new ConsentSecurityPolicy("base-uri", "'none'"));
        return View("Embedded", new EmbeddedPayModel(invoiceId, Protocol.Format(p.Amount), p.Currency,
            checkout, ReturnPath(invoiceId), Url.Action(nameof(Status), new { invoiceId })!));
    }

    // Read only BTCPay's already-verified state. Browsers cannot initiate extra API
    // polling, choose a redirect URL, or report a payment through this endpoint.
    [AllowAnonymous, HttpGet("~/plugins/whollycrypto/pay/{invoiceId}/status")]
    public async Task<IActionResult> Status(string invoiceId)
    {
        if (!ValidId(invoiceId)) return NotFound();
        var invoice = await invoices.GetInvoice(invoiceId);
        if (invoice is null || WhollyBridge.Details(invoice) is not { } p) return NotFound();
        return Json(new { returnToInvoice = ShouldReturn(invoice, p) });
    }

    [AllowAnonymous, HttpGet("~/plugins/whollycrypto/return/{invoiceId}")]
    [XFrameOptions(XFrameOptionsAttribute.XFrameOptions.SameOrigin)]
    public async Task<IActionResult> Return(string invoiceId, [FromServices] ContentSecurityPolicies policies)
    {
        if (!ValidId(invoiceId)) return NotFound();
        var invoice = await invoices.GetInvoice(invoiceId);
        if (invoice is null || WhollyBridge.Details(invoice) is null) return NotFound();
        policies.Add(new ConsentSecurityPolicy("frame-ancestors", "'self'"));
        return View("Return", new PayModel(invoiceId, "", "", ReturnPath(invoiceId), false, null));
    }

    public static bool ShouldReturn(InvoiceEntity invoice, PromptDetails p) => invoice.Archived
        || invoice.Status is not (InvoiceStatus.New or InvoiceStatus.Processing)
        || p.Review is not null || p.RemoteStatus is "settled" or "cancelled" or "invalid" or "expired";

    [AllowAnonymous, IgnoreAntiforgeryToken, HttpPost("~/plugins/whollycrypto/callback/{invoiceId}")]
    [RequestSizeLimit(Protocol.MaxBody)]
    public async Task<IActionResult> Callback(string invoiceId, CancellationToken ct)
    {
        if (!ValidId(invoiceId)) return NotFound();
        if (Request.Headers["Wholly-Signature"].Count != 1 || Request.Headers["Wholly-Event-Id"].Count != 1) return Unauthorized();
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192]; int count;
            while ((count = await Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + count > Protocol.MaxBody) return StatusCode(413);
                buffer.Write(chunk, 0, count);
            }
            await bridge.Callback(invoiceId, buffer.ToArray(), Request.Headers["Wholly-Signature"].ToString(),
                Request.Headers["Wholly-Event-Id"].ToString(), ct);
            return Ok(new { received = true, verification_pending = true });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Response.Headers.RetryAfter = (e is ConnectorException ce ? ce.RetryAfterSeconds : 60).ToString();
            return StatusCode(503, new { error = "Payment verification could not finish. Retry this delivery." });
        }
    }

    [HttpPost("~/stores/{storeId}/whollycrypto/invoices/{invoiceId}/refresh")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Refresh(string storeId, string invoiceId, CancellationToken ct, bool returnToPayments = false, string? search = null, string? filter = null, int page = 1)
    {
        var store = HttpContext.GetStoreDataOrNull();
        var invoice = ValidId(invoiceId) ? await invoices.GetInvoice(invoiceId) : null;
        if (store is null || store.Id != storeId || invoice?.StoreId != storeId) return NotFound();
        try
        {
            var p = WhollyBridge.Details(invoice);
            if (p?.RequestJson is null) throw new ConnectorException("No linked Wholly request exists yet. The customer must first continue with Wholly Crypto.");
            if (p?.Error is not null && p.NextCheck > DateTimeOffset.UtcNow)
                throw new ConnectorException("The API is in retry backoff. Next attempt: " + p.NextCheck.Value.ToString("u"));
            if (p?.LastAttempt > DateTimeOffset.UtcNow.AddSeconds(-10))
                throw new ConnectorException("This invoice was just checked. Wait a few seconds before checking again.");
            await bridge.Synchronize(invoiceId, false, ct);
            TempData["WhollyMessage"] = "Payment checked against Wholly. No manual paid status was applied.";
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        { TempData["WhollyMessage"] = Protocol.SafeError(e); }
        return returnToPayments ? RedirectToAction(nameof(Payments), new { storeId, search, filter, page })
            : RedirectToAction("Invoice", "UIInvoice", new { invoiceId });
    }

    private string ReturnPath(string id) => Request.PathBase + "/i/" + Uri.EscapeDataString(id);
    private static bool ValidId(string value) => Regex.IsMatch(value, @"\A[1-9A-HJ-NP-Za-km-z]{15,32}\z");
}
