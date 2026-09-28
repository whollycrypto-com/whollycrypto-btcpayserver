using BTCPayServer.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.WhollyCrypto;

// Uses the pinned BTCPay invoice schema; no separate payment ledger or migration.
// Store predicates are mandatory for operator views. No remote requests on page loads.
public sealed class ActivityRepository(ApplicationDbContextFactory factory)
{
    const string Source = """
        FROM "Invoices" i
        CROSS JOIN LATERAL (SELECT i."Blob2" #> '{prompts,WHOLLY-CRYPTO,details}' AS p) d
        """ + "\n";
    const string Begun = "p->>'RequestJson' IS NOT NULL";

    public async Task<PaymentsModel> List(string storeId, string? search, string? filter, int page, CancellationToken ct)
    {
        var model = new PaymentsModel { StoreId = storeId, Search = (search ?? "").Trim(),
            Filter = filter is "review" or "errors" or "pending" or "settled" ? filter : "all" };
        if (model.Search.Length > 100) model.Search = model.Search[..100];
        var where = """
            WHERE i."StoreDataId" = @storeId AND
            """ + " " + Begun + "\n" + """
             AND (@search = '' OR strpos(lower(i."Id"), lower(@search)) > 0
                 OR strpos(lower(coalesce(p->>'InvoiceId', p->>'CallbackInvoiceId', '')), lower(@search)) > 0
                 OR strpos(lower(coalesce(i."Blob2" #>> '{metadata,orderId}', '')), lower(@search)) > 0)
             AND (@filter = 'all' OR (@filter = 'review' AND p->>'Review' IS NOT NULL)
                 OR (@filter = 'errors' AND p->>'Error' IS NOT NULL)
                 OR (@filter = 'pending' AND (p->>'CallbackPending' = 'true' OR p->>'RemoteStatus' IN ('new','processing','not_started')))
                 OR (@filter = 'settled' AND p->>'RemoteStatus' = 'settled'))
            """;
        await using var context = factory.CreateContext();
        var db = context.Database.GetDbConnection();
        model.Total = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) " + Source + where,
            new { storeId, search = model.Search, filter = model.Filter }, commandTimeout: 10, cancellationToken: ct));
        model.Page = Math.Clamp(page, 1, model.Pages);
        model.Rows = (await db.QueryAsync<LinkedPayment>(new CommandDefinition("""
            SELECT i."Id", i."Status", i."Created", i."Blob2" #>> '{metadata,orderId}' AS "OrderId", p::text AS "DetailsJson"
            """ + "\n" + Source + where + " ORDER BY i.\"Created\" DESC, i.\"Id\" DESC LIMIT 20 OFFSET @offset",
            new { storeId, search = model.Search, filter = model.Filter, offset = (model.Page - 1) * 20 },
            commandTimeout: 10, cancellationToken: ct))).ToList();
        return model;
    }

    public async Task<ConnectionActivity> Health(string storeId, string connectionId, CancellationToken ct)
    {
        await using var context = factory.CreateContext();
        return await context.Database.GetDbConnection().QuerySingleAsync<ConnectionActivity>(new CommandDefinition("""
            SELECT max((p->>'CreatedViaApiAt')::bigint) AS "LastWrite",
                max((p->>'LastCheck')::bigint) AS "LastCheck", max((p->>'LastCallback')::bigint) AS "LastCallback",
                max((p->>'CallbackVerifiedAt')::bigint) AS "CallbackVerifiedAt",
                count(*) FILTER (WHERE p->>'CallbackPending' = 'true') AS "Pending",
                count(*) FILTER (WHERE p->>'Error' IS NOT NULL) AS "Errors"
            """ + "\n" + Source + " WHERE i.\"StoreDataId\" = @storeId AND p->>'ConnectionId' = @connectionId",
            new { storeId, connectionId }, commandTimeout: 10, cancellationToken: ct));
    }

    public async Task<string[]> PendingCallbacks(CancellationToken ct)
    {
        await using var context = factory.CreateContext();
        return (await context.Database.GetDbConnection().QueryAsync<string>(new CommandDefinition(
            "SELECT i.\"Id\" " + Source + """
             WHERE p->>'CallbackPending' = 'true'
               AND (p->>'NextCheck' IS NULL OR (p->>'NextCheck')::bigint <= @now)
             ORDER BY (p->>'NextCheck')::bigint NULLS FIRST, i."Created" LIMIT 20
            """, new { now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, commandTimeout: 10, cancellationToken: ct))).ToArray();
    }
}
