using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.DataProtection;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class Connections(StoreRepository stores, IDataProtectionProvider protection)
{
    public async Task<string> Save(string storeId, Connection connection)
    {
        Protocol.ValidateConnection(connection);
        var id = Guid.NewGuid().ToString("D");
        var value = protection.CreateProtector("WhollyCrypto.Connection.v1", storeId, id)
            .Protect(JsonConvert.SerializeObject(connection));
        await stores.UpdateSetting(storeId, "WhollyCrypto.Connection." + id, new SavedConnection { ProtectedValue = value });
        return id;
    }

    public async Task<Connection> Get(string storeId, string id)
    {
        Protocol.Uuid(id);
        var saved = await stores.GetSettingAsync<SavedConnection>(storeId, "WhollyCrypto.Connection." + id)
            ?? throw new ConnectorException("The original Wholly connection is missing. Restore the BTCPay database and data-protection keys.");
        var json = protection.CreateProtector("WhollyCrypto.Connection.v1", storeId, id).Unprotect(saved.ProtectedValue);
        var connection = JsonConvert.DeserializeObject<Connection>(json) ?? throw new ConnectorException("Invalid saved connection.");
        Protocol.ValidateConnection(connection);
        return connection;
    }
}
