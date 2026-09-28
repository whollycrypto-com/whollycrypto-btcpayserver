using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.WhollyCrypto;

// Session advisory locks coordinate requests/workers across BTCPay processes.
// No transaction is held open while contacting the remote merchant.
public sealed class InvoiceLock(ApplicationDbContextFactory factory)
{
    public async Task<IAsyncDisposable> Acquire(string invoiceId, CancellationToken ct)
    {
        var context = factory.CreateContext();
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("WhollyCrypto:" + invoiceId)));
        try
        {
            await context.Database.OpenConnectionAsync(ct);
            var conn = context.Database.GetDbConnection();
            for (var i = 0; i < 25; i++)
            {
                if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_lock(@key)", new { key }, cancellationToken: ct)))
                    return new Lease(context, key);
                await Task.Delay(200, ct);
            }
            throw new ConnectorException("This payment is already being checked. Please retry shortly.", 30);
        }
        catch { await context.DisposeAsync(); throw; }
    }

    private sealed class Lease(ApplicationDbContext context, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await context.Database.GetDbConnection().ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key }); }
            finally { await context.DisposeAsync(); }
        }
    }
}
