using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class Plugin : BaseBTCPayServerPlugin
{
    public override string Identifier => "BTCPayServer.Plugins.WhollyCrypto";
    public override string Name => "Wholly Crypto";
    public override string Description => "Stablecoins and other networks through your own Wholly Crypto installation.";
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = "BTCPayServer", Condition = ">=2.4.4 <2.5.0" }];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<Connections>();
        services.AddSingleton<InvoiceLock>();
        services.AddSingleton<IWhollyClient, WhollyClient>();
        services.AddSingleton<WhollyPaymentHandler>();
        services.AddSingleton<IPaymentMethodHandler>(p => p.GetRequiredService<WhollyPaymentHandler>());
        services.AddSingleton<ICheckoutModelExtension, WhollyCheckout>();
        services.AddSingleton<IPaymentLinkExtension, WhollyCheckout>();
        services.AddSingleton(new PrettyNameProvider.UntranslatedPrettyName(WhollyPaymentHandler.Method, "Stablecoins & crypto · Wholly"));
        services.AddSingleton<WhollyBridge>();
        services.AddHostedService<WhollyWorker>();
        services.AddUIExtension("store-category-nav", "WhollyNav");
        services.AddUIExtension("checkout-end", "WhollyCheckout");
        services.AddUIExtension("store-invoices-payments", "WhollyPaymentDetails");
    }
}
