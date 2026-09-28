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
    WhollyPaymentHandler handler, InvoiceRepository invoices, WhollyBridge bridge) : Controller
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
        try
        {
            if (!ModelState.IsValid) return CleanSettings(vm);
            var old = oldConfig is null ? null : await connections.Get(storeId, oldConfig.ConnectionId);
            var c = new Connection { ApiOrigin = vm.ApiOrigin, CheckoutOrigin = vm.CheckoutOrigin,
                ProjectId = vm.ProjectId, StoreId = vm.WhollyStoreId, EmbedCheckout = vm.EmbedCheckout,
                ApiKey = string.IsNullOrWhiteSpace(vm.ApiKey) ? old?.ApiKey ?? "" : vm.ApiKey.Trim(),
                IpnSecret = string.IsNullOrWhiteSpace(vm.IpnSecret) ? old?.IpnSecret ?? "" : vm.IpnSecret.Trim() };
            Protocol.ValidateConnection(c);
            if (command == "test")
            {
                await client.Request(c, $"/v1/projects/{c.ProjectId}/stores/{c.StoreId}/payment-assets", null, null, ct);
                vm.Message = "Read access to this Wholly store works. This does not test invoice creation or incoming callbacks. Changes have not been saved.";
                return CleanSettings(vm);
            }
            if (command != "save") return BadRequest();
            var id = await connections.Save(storeId, c);
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
            return Ok(new { received = true });
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
    public async Task<IActionResult> Refresh(string storeId, string invoiceId, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        var invoice = ValidId(invoiceId) ? await invoices.GetInvoice(invoiceId) : null;
        if (store is null || store.Id != storeId || invoice?.StoreId != storeId) return NotFound();
        try { await bridge.Synchronize(invoiceId, false, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        { TempData["WhollyMessage"] = Protocol.SafeError(e); }
        return RedirectToAction("Invoice", "UIInvoice", new { invoiceId });
    }

    private string ReturnPath(string id) => Request.PathBase + "/i/" + Uri.EscapeDataString(id);
    private static bool ValidId(string value) => Regex.IsMatch(value, @"\A[1-9A-HJ-NP-Za-km-z]{15,32}\z");
}
