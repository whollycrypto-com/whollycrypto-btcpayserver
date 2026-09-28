using BTCPayServer.Plugins.PluginManagement.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.WhollyCrypto;

// BTCPay 2.4.4 normally obtains card resources from its remote directory, which
// does not contain manually uploaded plugins. Supply only our own card metadata.
public sealed class PluginResourcesFilter : IResultFilter
{
    public const string Repository = "https://github.com/whollycrypto-com/whollycrypto-btcpayserver";
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ViewResult { Model: InstalledPluginsViewModel model }) Decorate(model);
    }
    public static void Decorate(InstalledPluginsViewModel model)
    {
        foreach (var card in model.InstalledPlugins.Where(c => c.Current?.Identifier == "BTCPayServer.Plugins.WhollyCrypto"))
        {
            card.Current.Documentation = Repository + "#setup";
            card.Current.Source = Repository;
            card.Current.Author = "Wholly Crypto";
            card.Current.AuthorLink = "https://www.whollycrypto.com/";
        }
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}
