using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class Connection
{
    public string ApiOrigin { get; set; } = "";
    public string CheckoutOrigin { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string IpnSecret { get; set; } = "";
    public bool EmbedCheckout { get; set; }
}

public sealed class MethodConfig
{
    public string ConnectionId { get; set; } = "";
}

public sealed class SavedConnection
{
    public string ProtectedValue { get; set; } = "";
}

// Saved in BTCPay's invoice prompt BEFORE the first remote creation request.
// Secrets are held separately in the store's protected, versioned connection.
public sealed class PromptDetails
{
    public string ConnectionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string? RequestJson { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? InvoiceId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string RemoteStatus { get; set; } = "not_started";
    public string AmountStatus { get; set; } = "none";
    public long Sequence { get; set; }
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset? LastCheck { get; set; }
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset? NextCheck { get; set; }
    public string? Error { get; set; }
    public string? Review { get; set; }
}

public sealed class PaymentDetails
{
    public string InvoiceId { get; set; } = "";
    public string? Chain { get; set; }
    public string? Asset { get; set; }
    public string? AssetAmount { get; set; }
    public string? Resolution { get; set; }
}

public sealed class SettingsModel
{
    public string StoreId { get; set; } = "";
    public bool Enabled { get; set; }
    [Required, Display(Name = "Wholly API URL")]
    public string ApiOrigin { get; set; } = "";
    [Required, Display(Name = "Wholly checkout URL")]
    public string CheckoutOrigin { get; set; } = "";
    [Required, Display(Name = "Project API ID")]
    public string ProjectId { get; set; } = "";
    [Required, Display(Name = "Store API ID")]
    public string WhollyStoreId { get; set; } = "";
    [Display(Name = "API credential")]
    public string? ApiKey { get; set; }
    [Display(Name = "Store IPN signing secret")]
    public string? IpnSecret { get; set; }
    public bool HasSavedConnection { get; set; }
    public bool EmbedCheckout { get; set; }
    public string? Message { get; set; }
}

public sealed record PayModel(string InvoiceId, string Amount, string Currency,
    string ReturnPath, bool CanStart, string? Message);

public sealed record EmbeddedPayModel(string InvoiceId, string Amount, string Currency,
    string CheckoutUrl, string ReturnPath, string StatusPath);

public sealed class ConnectorException(string message, int retryAfterSeconds = 60) : Exception(message)
{
    public int RetryAfterSeconds { get; } = Math.Clamp(retryAfterSeconds, 30, 3600);
}
