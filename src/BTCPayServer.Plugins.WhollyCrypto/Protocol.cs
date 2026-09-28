using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.WhollyCrypto;

public static partial class Protocol
{
    public const int MaxBody = 2 * 1024 * 1024;
    public static readonly string[] Statuses = ["new", "processing", "settled", "expired", "invalid", "cancelled"];

    public static string Decimal(string value)
    {
        if (!Regex.IsMatch(value, @"\A[0-9]{1,48}(?:\.[0-9]{1,30})?\z", RegexOptions.CultureInvariant))
            throw new ConnectorException("Invalid decimal amount received.");
        var parts = value.Split('.');
        var whole = parts[0].TrimStart('0');
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        return (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? "" : "." + fraction);
    }

    public static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static string Uuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new ConnectorException("Copy the Project and Store API IDs, not their readable names.");
        return id.ToString("D");
    }

    public static Uri Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0
            || uri.AbsolutePath != "/" || value.Any(c => c <= 32 || c == 127 || c == '\\')
            || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.'))
            throw new ConnectorException("Use a public HTTPS hostname on port 443, without a path, credentials or query.");
        return uri;
    }

    public static void ValidateConnection(Connection c)
    {
        c.ApiOrigin = Origin(c.ApiOrigin.Trim()).GetLeftPart(UriPartial.Authority);
        c.CheckoutOrigin = Origin(c.CheckoutOrigin.Trim()).GetLeftPart(UriPartial.Authority);
        c.ProjectId = Uuid(c.ProjectId.Trim()); c.StoreId = Uuid(c.StoreId.Trim());
        foreach (var secret in new[] { c.ApiKey, c.IpnSecret })
            if (secret.Length is < 16 or > 4096 || secret.Any(ch => ch < 33 || ch > 126))
                throw new ConnectorException("Enter the API credential and the Store IPN signing secret.");
        if (c.AcceptedMethods is { } selected)
        {
            if (selected.Count is < 1 or > 64 || selected.Select(x => x.Key).Distinct().Count() != selected.Count)
                throw new ConnectorException("Choose between 1 and 64 unique payment methods.");
            foreach (var a in selected)
            {
                if (!Regex.IsMatch(a.Chain, @"\A[a-z0-9-]{1,64}\z")) throw new ConnectorException("Invalid payment network.");
                if (!a.Lightning) Uuid(a.Key);
                else if (a.Chain != "bitcoin") throw new ConnectorException("Invalid Lightning selection.");
            }
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 192 && b[1] == 0
                || b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Global unicast only; exclude documentation and IPv4 transition mechanisms.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 1 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8))
            && !(b[0] == 0x20 && b[1] == 2);
    }

    public static JObject Json(byte[] raw)
    {
        if (raw.Length > MaxBody) throw new ConnectorException("Response exceeds the size limit.");
        using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(raw)))
        { MaxDepth = 32, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
        var obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new ConnectorException("Unexpected data after the JSON object.");
        return obj;
    }

    public static string Required(JObject obj, string field) => obj[field]?.Type == JTokenType.String
        ? obj[field]!.Value<string>()! : throw new ConnectorException("Missing invoice identity or amount.");

    public static void Match(PromptDetails p, Connection c, string btcpayInvoiceId, JObject invoice)
    {
        var id = Uuid(Required(invoice, "invoice_id"));
        if (p.InvoiceId is not null && id != p.InvoiceId || Required(invoice, "project_id") != c.ProjectId
            || Required(invoice, "store_id") != c.StoreId || Required(invoice, "order_id") != "btcpay:" + btcpayInvoiceId
            || Required(invoice, "currency") != p.Currency || Decimal(Required(invoice, "amount")) != Format(p.Amount)
            || !Statuses.Contains(Required(invoice, "status"))
            || !new[] { "none", "partial", "paid", "overpaid" }.Contains(Required(invoice, "amount_status"))
            || !new[] { "on_time", "late" }.Contains(Required(invoice, "timing_status"))
            || !new[] { "automatic", "manually_settled", "manually_invalidated" }.Contains(Required(invoice, "resolution"))
            || invoice["sequence"]?.Type != JTokenType.Integer || invoice["sequence"]!.Value<long>() < Math.Max(1, p.Sequence))
            throw new ConnectorException("Wholly invoice does not match the saved BTCPay payment request.");
    }

    public static string Checkout(Connection c, string invoiceId, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0
            || url.GetLeftPart(UriPartial.Authority) != c.CheckoutOrigin || url.Query.Length != 0 || url.Fragment.Length != 0
            || url.AbsolutePath != "/invoice/" + invoiceId)
            throw new ConnectorException("Checkout link does not match the configured Wholly checkout domain.");
        return url.AbsoluteUri;
    }

    public static bool VerifySignature(byte[] body, string header, string secret, DateTimeOffset now)
    {
        if (body.Length > MaxBody || header.Length > 160) return false;
        var match = Regex.Match(header, @"\At=([0-9]{1,12}),v1=([a-f0-9]{64})\z", RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var timestamp)
            || Math.Abs(now.ToUnixTimeSeconds() - timestamp) > 300) return false;
        var prefix = Encoding.UTF8.GetBytes(match.Groups[1].Value + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0); body.CopyTo(signed, prefix.Length);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return CryptographicOperations.FixedTimeEquals(mac, Convert.FromHexString(match.Groups[2].Value));
    }

    public static string SafeError(Exception error) => error is ConnectorException known ? known.Message
        : "Wholly Crypto could not be reached or verified. Check the saved connection and retry; the same request will be reused.";
}
