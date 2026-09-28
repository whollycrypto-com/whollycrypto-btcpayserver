using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.WhollyCrypto;

public static class PaymentSelection
{
    public static List<AssetChoice> Catalog(JObject response)
    {
        if (response["data"] is not JArray rows || rows.Count > 5000)
            throw new ConnectorException("The payment-method catalogue response is invalid. Update Wholly and retry.");
        var choices = new List<AssetChoice>();
        foreach (var row in rows.OfType<JObject>().Where(x => x["selected"]?.Value<bool>() == true))
        {
            var a = row["asset"] as JObject ?? throw new ConnectorException("Invalid payment asset.");
            var chain = Protocol.Required(a, "chain_slug");
            if (!Regex.IsMatch(chain, @"\A[a-z0-9-]{1,64}\z")) throw new ConnectorException("Invalid payment network.");
            var ready = row["receive_readiness"]?["invoice_creatable"]?.Value<bool>() ?? (string?)row["wallet_readiness"] == "ready";
            choices.Add(new(Protocol.Uuid(Protocol.Required(a, "id")), chain, Protocol.Required(a, "symbol"),
                (string?)a["name"] ?? Protocol.Required(a, "symbol"), (string?)a["contract_address"], ready,
                ready ? "Accepted by store" : "Check wallet and store setup in Wholly"));
        }
        if (response["lightning"]?["enabled"]?.Value<bool>() == true)
            choices.Add(new("bitcoin:lightning", "bitcoin", "BTC", "Bitcoin Lightning", null,
                response["lightning"]?["ready"]?.Value<bool>() == true, "Lightning connection must be ready"));
        if (choices.Select(x => x.Key).Distinct().Count() != choices.Count)
            throw new ConnectorException("Duplicate payment methods returned by Wholly.");
        return choices.OrderBy(x => x.Chain).ThenBy(x => x.Symbol).ToList();
    }

    public static List<AssetChoice> Select(IEnumerable<string> keys, List<AssetChoice> catalog)
    {
        var selected = keys.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length is < 1 or > 64) throw new ConnectorException("Choose between 1 and 64 store-accepted payment methods.");
        var result = selected.Select(key => catalog.SingleOrDefault(x => x.Key == key)
            ?? throw new ConnectorException("A chosen asset is no longer accepted. Refresh payment methods and choose again.")).ToList();
        if (result.Any(x => !x.Ready)) throw new ConnectorException("A chosen method needs setup in Wholly. Fix it or choose another accepted method.");
        return result;
    }

    public static JArray Request(List<AssetChoice> selected)
    {
        var result = new JArray(selected.Where(x => !x.Lightning).GroupBy(x => x.Chain).Select(g => new JObject
        { ["chain_slug"] = g.Key, ["asset_ids"] = new JArray(g.Select(x => x.Key)) }));
        if (selected.Any(x => x.Lightning)) result.Add(new JObject { ["chain_slug"] = "bitcoin", ["payment_rail"] = "lightning" });
        return result;
    }

    // The API can fall back to store defaults for an unmatched selection. Fail closed
    // before presenting a checkout if policy changed between preflight and creation.
    public static void VerifyInvoice(Connection c, JObject remote)
    {
        if (c.AcceptedMethods is null) return;
        if (remote["payment_intents"] is not JArray methods || methods.Count == 0)
            throw new ConnectorException("Cannot verify the selected checkout methods. Check the linked invoice in Wholly; no second invoice will be created.");
        foreach (var method in methods)
        {
            var lightning = (string?)method["payment_rail"] == "lightning";
            if (!c.AcceptedMethods.Any(a => a.Lightning == lightning && a.Chain == (string?)method["chain_slug"]
                && (lightning || a.Key == (string?)method["asset_id"])))
                throw new ConnectorException("Wholly returned a payment method outside this connection's selection. Checkout is blocked; review the linked invoice and store configuration.");
        }
    }
}
