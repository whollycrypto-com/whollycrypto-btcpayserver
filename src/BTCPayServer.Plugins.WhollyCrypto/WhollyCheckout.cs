using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class WhollyCheckout : ICheckoutModelExtension, IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => WhollyPaymentHandler.Method;
    public string Image => "/Resources/img/whollycrypto.svg";
    public string Badge => "";
    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        context.Model.CheckoutBodyComponentName = "WhollyCryptoCheckout";
        context.Model.AdditionalData["whollyStartUrl"] = context.UrlHelper.Action("Start", "WhollyCrypto",
            new { invoiceId = context.InvoiceEntity.Id })!;
        context.Model.AdditionalData["whollyLogoUrl"] = context.UrlHelper.Content("~/Resources/img/whollycrypto-horizontal-color.png");
        context.Model.InvoiceBitcoinUrl = null;
        context.Model.InvoiceBitcoinUrlQR = null;
        context.Model.ShowPayInWalletButton = false;
    }

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) => prompt.Destination;
}
