using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class Plugin : BaseBTCPayServerPlugin
{
    public override string Identifier => "BTCPayServer.Plugins.WhollyCrypto";
    public override string Name => "Wholly Crypto";
    public override Version Version
    {
        get { var v = typeof(Plugin).Assembly.GetName().Version!; return new Version(v.Major, v.Minor, v.Build); }
    }
    public override string Description => "Stablecoins and other networks through your own Wholly Crypto installation. To connect: select a BTCPay store, then Plugins → Wholly Crypto. Setup guide, connection health and linked-payment diagnostics included. Validate your payment flow before live use.";
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [new() { Identifier = "BTCPayServer", Condition = ">=2.4.4 <2.5.0" }];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<Connections>();
        services.AddSingleton<InvoiceLock>();
        services.AddSingleton<ActivityRepository>();
        services.AddSingleton<IWhollyClient, WhollyClient>();
        services.AddSingleton<WhollyPaymentHandler>();
        services.AddSingleton<IPaymentMethodHandler>(p => p.GetRequiredService<WhollyPaymentHandler>());
        services.AddSingleton<ICheckoutModelExtension, WhollyCheckout>();
        services.AddSingleton<IPaymentLinkExtension, WhollyCheckout>();
        services.AddSingleton(new PrettyNameProvider.UntranslatedPrettyName(WhollyPaymentHandler.Method, "Stablecoins & crypto · Wholly"));
        services.AddSingleton<WhollyBridge>();
        services.AddHostedService<WhollyWorker>();
        services.Configure<MvcOptions>(options => options.Filters.Add(new PluginResourcesFilter()));
        services.AddUIExtension("store-category-nav", "WhollyNav");
        services.AddUIExtension("store-integrations-nav", "WhollyIntegrationNav");
        services.AddUIExtension("checkout-end", "WhollyCheckout");
        services.AddUIExtension("store-invoices-payments", "WhollyPaymentDetails");
    }
}
