using System.Net;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.WhollyCrypto;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Notifications;
using BTCPayServer.HostedServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;

var tests = new Checks();
tests.Unit();
if (args.Contains("--database")) await tests.Database();
Console.WriteLine($"PASS: {tests.Count} checks; database={(args.Contains("--database") ? "executed" : "not requested")}.");

sealed class Checks
{
    public int Count;
    void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Count++; }
    void Reject(Action action, string name)
    {
        try { action(); } catch { Count++; return; }
        throw new Exception("FAIL: accepted " + name);
    }
    async Task RejectAsync(Func<Task> action, string name)
    {
        try { await action(); } catch { Count++; return; }
        throw new Exception("FAIL: accepted " + name);
    }
    static Connection Connection() => new() { ApiOrigin = "https://api.example.com", CheckoutOrigin = "https://pay.example.com",
        ProjectId = "11111111-1111-4111-8111-111111111111", StoreId = "22222222-2222-4222-8222-222222222222",
        ApiKey = "synthetic-api-credential-for-tests", IpnSecret = "synthetic-ipn-secret-for-tests" };
    static PromptDetails Prompt() => new() { Amount = 25m, Currency = "EUR", InvoiceId = "33333333-3333-4333-8333-333333333333" };
    static JObject Remote(string order = "test") => new()
    {
        ["invoice_id"] = Prompt().InvoiceId, ["project_id"] = Connection().ProjectId, ["store_id"] = Connection().StoreId,
        ["amount"] = "25.00", ["currency"] = "EUR", ["order_id"] = "btcpay:" + order, ["status"] = "new",
        ["amount_status"] = "none", ["timing_status"] = "on_time", ["resolution"] = "automatic", ["sequence"] = 1
    };
    static string Sign(byte[] raw, string secret, DateTimeOffset now) => "t=" + now.ToUnixTimeSeconds() + ",v1="
        + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(now.ToUnixTimeSeconds() + ".").Concat(raw).ToArray())).ToLowerInvariant();

    public void Unit()
    {
        Check(Protocol.Decimal("00025.000") == "25", "exact decimal normalization");
        Check(Protocol.Decimal("0.000000000000000001") == "0.000000000000000001", "no floating point");
        foreach (var value in new[] { "-1", "1e3", "NaN", "25,0", "+1", " 2", "1.", ".1", "" }) Reject(() => Protocol.Decimal(value), "bad decimal");
        foreach (var value in new[] { "http://api.example.com", "https://user:pass@api.example.com", "https://localhost", "https://127.0.0.1",
            "https://api.example.com/v1", "https://api.example.com?a=b", "https://api.example.com/#a", "https://api.example.com:8443", "https://api.example.com\\x" })
            Reject(() => Protocol.Origin(value), "unsafe origin");
        Protocol.ValidateConnection(Connection()); Count++;
        foreach (var ip in new[] { "127.0.0.1", "10.2.3.4", "169.254.169.254", "192.168.1.3", "172.16.0.1", "100.64.1.1", "0.0.0.0",
            "198.18.0.1", "224.0.0.1", "::1", "::ffff:127.0.0.1", "fc00::1", "fe80::1", "2001:db8::1", "2002:7f00:1::" })
            Check(!Protocol.IsPublic(IPAddress.Parse(ip)), "non-public IP blocked");
        Check(Protocol.IsPublic(IPAddress.Parse("1.1.1.1")), "public IPv4");
        Check(Protocol.IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "public IPv6");
        Protocol.Match(Prompt(), Connection(), "test", Remote()); Count++;
        foreach (var field in new[] { "invoice_id", "project_id", "store_id", "order_id", "amount", "currency", "status", "amount_status", "timing_status", "resolution" })
        {
            var bad = Remote(); bad[field] = "unexpected";
            Reject(() => Protocol.Match(Prompt(), Connection(), "test", bad), "mismatched " + field);
        }
        var stale = Prompt(); stale.Sequence = 2; Reject(() => Protocol.Match(stale, Connection(), "test", Remote()), "stale API state");
        var id = Prompt().InvoiceId!;
        Check(Protocol.Checkout(Connection(), id, "https://pay.example.com/invoice/" + id).EndsWith(id), "pinned checkout");
        foreach (var url in new[] { "https://evil.example/invoice/" + id, "https://pay.example.com/invoice/other", "https://pay.example.com/invoice/" + id + "?x=1" })
            Reject(() => Protocol.Checkout(Connection(), id, url), "checkout redirect");
        var raw = Encoding.UTF8.GetBytes("{\"status\":\"settled\"}"); var now = DateTimeOffset.UtcNow;
        var header = Sign(raw, Connection().IpnSecret, now);
        Check(Protocol.VerifySignature(raw, header, Connection().IpnSecret, now), "valid raw-body signature");
        Check(!Protocol.VerifySignature(raw, header, "another-synthetic-secret", now), "wrong signature key");
        Check(!Protocol.VerifySignature(raw.Concat(new byte[] { 32 }).ToArray(), header, Connection().IpnSecret, now), "body tampering");
        Check(!Protocol.VerifySignature(raw, header, Connection().IpnSecret, now.AddMinutes(6)), "expired signature");
        Check(!Protocol.VerifySignature(raw, header, Connection().IpnSecret, now.AddMinutes(-6)), "future signature");
        Check(!Protocol.VerifySignature(raw, header + ",v1=bad", Connection().IpnSecret, now), "duplicate signature field");
        Reject(() => Protocol.Json(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")), "ambiguous JSON");
        Reject(() => Protocol.Json(Encoding.UTF8.GetBytes("{}{}")), "trailing JSON");
        var p = Prompt(); p.ConnectionId = "secret-reference"; p.RequestJson = "private payload"; p.RequestId = "private retry key"; p.Error = "operator only";
        new WhollyPaymentHandler(null!, null!).StripDetailsForNonOwner(p);
        Check(p.ConnectionId == "" && p.RequestJson is null && p.RequestId == "" && p.Error is null, "public prompt strips private fields");
    }

    public async Task Database()
    {
        var cs = Environment.GetEnvironmentVariable("WHOLLY_TEST_DATABASE") ?? throw new Exception("Set WHOLLY_TEST_DATABASE for a disposable database.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        if (settings.Database is null || !settings.Database.StartsWith("wholly_btcpay_test_", StringComparison.Ordinal)) throw new Exception("Refusing non-test database.");
        var factory = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = cs }), NullLoggerFactory.Instance);
        await using (var db = factory.CreateContext()) await db.Database.MigrateAsync();
        var aggregator = new EventAggregator(new Logs());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new InvoiceRepository(factory, aggregator);
        var storeRepository = new StoreRepository(factory, new JsonSerializerSettings(), aggregator, new SettingsRepository(factory, aggregator, cache));
        var conn = new Connections(storeRepository, new EphemeralDataProtectionProvider());
        var store = new BTCPayServer.Data.StoreData { Id = "WhollyFixtureStore", StoreName = "Synthetic fixture" };
        store.SetStoreBlob(new StoreBlob());
        await using (var db = factory.CreateContext()) { db.Stores.Add(store); await db.SaveChangesAsync(); }
        var connectionId = await conn.Save(store.Id, Connection());
        var currencies = new CurrencyNameTable([new InMemoryCurrencyDataProvider([
            new CurrencyData { Code = "EUR", Crypto = false, Divisibility = 2 },
            new CurrencyData { Code = "BTC", Crypto = true, Divisibility = 8 }])], NullLogger<CurrencyNameTable>.Instance);
        await currencies.ReloadCurrencyData(default);
        var handler = new WhollyPaymentHandler(conn, currencies);
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        store.SetPaymentMethodConfig(handler, new MethodConfig { ConnectionId = connectionId });
        await storeRepository.UpdateStore(store);
        var fake = new FakeClient();
        var paymentService = new PaymentService(aggregator, factory, handlers, repository);
        var bridge = new WhollyBridge(repository, storeRepository, conn, fake, new InvoiceLock(factory), handler, paymentService, aggregator);
        using var services = new ServiceCollection().BuildServiceProvider();
        var watcher = new InvoiceWatcher(repository, aggregator, null!,
            new NotificationSender(factory, services.GetRequiredService<IServiceScopeFactory>(), new NotificationManager(factory, cache, [], aggregator)),
            paymentService, handlers, new Logs());
        await watcher.StartAsync(default);
        async Task AwaitStatus(string id, InvoiceStatus status)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if ((await repository.GetInvoice(id)).Status == status) { Count++; return; }
                await Task.Delay(50);
            }
            throw new Exception("FAIL: upstream invoice watcher did not reach " + status);
        }

        async Task<InvoiceEntity> NewInvoice()
        {
            var i = repository.CreateNewInvoice(store.Id); i.Price = 25; i.Currency = "EUR"; i.Status = InvoiceStatus.New;
            i.ExpirationTime = DateTimeOffset.UtcNow.AddHours(1); i.MonitoringExpiration = DateTimeOffset.UtcNow.AddDays(1);
            i.ServerUrl = "https://btcpay.example.com/"; i.Metadata.OrderId = "synthetic-order";
            var context = new PaymentMethodContext(store, store.GetStoreBlob(), JObject.FromObject(new MethodConfig { ConnectionId = connectionId }),
                handler, i, new InvoiceLogs());
            await handler.BeforeFetchingRates(context); await handler.ConfigurePrompt(context);
            Check(context.Prompt.Divisibility == 2, "fiat checkout keeps normal currency precision");
            i.SetPaymentPrompt(handler.PaymentMethodId, context.Prompt);
            var creation = new InvoiceCreationContext(store, store.GetStoreBlob(), i, new InvoiceLogs(), handlers, null);
            await repository.CreateInvoiceAsync(creation);
            return i;
        }

        var invoice = await NewInvoice();
        Check(fake.Calls == 0, "invoice creation does not call Wholly");
        var begin = await bridge.Synchronize(invoice.Id, true, default);
        Check(begin.InvoiceId is not null && fake.Creates == 1, "create on selection");
        var request = fake.LastBody; var key = fake.LastKey;
        await bridge.Synchronize(invoice.Id, true, default);
        Check(fake.Creates == 1, "second click reuses linked invoice");
        Check((await repository.GetInvoice(invoice.Id)).GetPayments(false).Count() == 0, "no payment on new or redirect");
        fake.Status = "processing"; fake.AmountStatus = "partial";
        await bridge.Synchronize(invoice.Id, false, default);
        Check((await repository.GetInvoice(invoice.Id)).GetPayments(true).Count() == 0, "partial does not fulfil");
        fake.AmountStatus = "paid";
        await bridge.Synchronize(invoice.Id, false, default);
        var received = (await repository.GetInvoice(invoice.Id)).GetPayments(false).Single();
        Check(received.Status == PaymentStatus.Processing && received.Value == 25 && received.Currency == "EUR", "confirmed amount uses original fiat accounting");
        await AwaitStatus(invoice.Id, InvoiceStatus.Processing);
        fake.Status = "settled";
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => bridge.Synchronize(invoice.Id, false, default)));
        var done = await repository.GetInvoice(invoice.Id);
        Check(done.GetPayments(false).Count() == 1 && done.GetPayments(false).Single().Status == PaymentStatus.Settled, "parallel callbacks cannot double record");
        Check(done.GetPayments(false).Single().PaymentMethodId == WhollyPaymentHandler.Method, "not a fictitious BTC payment");
        await AwaitStatus(invoice.Id, InvoiceStatus.Settled);
        fake.Status = "invalid";
        await bridge.Synchronize(invoice.Id, false, default);
        var reversed = await repository.GetInvoice(invoice.Id);
        Check(WhollyBridge.Details(reversed)!.Review is not null && reversed.GetPayments(false).Single().Status == PaymentStatus.Unaccounted, "reversal flags review and stops accounting");

        fake.Status = "new"; fake.AmountStatus = "none";
        var lost = await NewInvoice(); fake.LoseNext = true;
        await RejectAsync(() => bridge.Synchronize(lost.Id, true, default), "lost response");
        var saved = WhollyBridge.Details(await repository.GetInvoice(lost.Id))!;
        var lostBody = fake.LastBody; var lostKey = fake.LastKey;
        Check(saved.RequestJson == lostBody && saved.InvoiceId is null, "persist request before remote side effect");
        var restarted = new WhollyBridge(new InvoiceRepository(factory, aggregator), storeRepository, conn, fake, new InvoiceLock(factory), handler, paymentService, aggregator);
        await restarted.Synchronize(lost.Id, true, default);
        Check(fake.LastBody == lostBody && fake.LastKey == lostKey, "restart retries exact bytes and key");

        var wrong = await NewInvoice(); fake.WrongAmount = true;
        await RejectAsync(() => bridge.Synchronize(wrong.Id, true, default), "forged amount");
        Check((await repository.GetInvoice(wrong.Id)).GetPayments(false).Count() == 0, "mismatched invoice not recorded");
        fake.WrongAmount = false;
        var late = await NewInvoice(); await bridge.Synchronize(late.Id, true, default);
        fake.Status = "settled"; fake.AmountStatus = "paid"; fake.Timing = "late";
        var lateDetails = await bridge.Synchronize(late.Id, false, default);
        Check(lateDetails.Review is not null && (await repository.GetInvoice(late.Id)).GetPayments(true).Count() == 0, "late payments require review");
        fake.Timing = "on_time";
        var manual = await NewInvoice(); fake.Status = "new"; fake.AmountStatus = "none";
        await bridge.Synchronize(manual.Id, true, default);
        fake.Status = "settled"; fake.AmountStatus = "paid"; fake.Resolution = "manually_settled";
        Check((await bridge.Synchronize(manual.Id, false, default)).Review is not null, "manual settlement requires review");
        fake.Resolution = "automatic";

        fake.Status = "new"; fake.AmountStatus = "none";
        var otherPaid = await NewInvoice(); await bridge.Synchronize(otherPaid.Id, true, default);
        await using (var db = factory.CreateContext())
        {
            db.Payments.Add(new PaymentData { Id = "synthetic-other-method", InvoiceDataId = otherPaid.Id,
                PaymentMethodId = "BTC-CHAIN", Currency = "EUR", Amount = 25, Status = PaymentStatus.Settled,
                Created = DateTimeOffset.UtcNow, Blob2 = "{}" });
            await db.SaveChangesAsync();
        }
        fake.Status = "settled"; fake.AmountStatus = "paid";
        Check((await bridge.Synchronize(otherPaid.Id, false, default)).Review is not null, "two payment methods require review");
        Check((await repository.GetInvoice(otherPaid.Id)).GetPayments(false).Single(p => p.PaymentMethodId == WhollyPaymentHandler.Method).Status == PaymentStatus.Unaccounted,
            "do not account second payment on top of Bitcoin");

        var locked = new InvoiceLock(factory);
        await using (await locked.Acquire("lease-fixture", default))
        {
            using var cancellation = new CancellationTokenSource(100);
            await RejectAsync(async () => { await using var impossible = await new InvoiceLock(factory).Acquire("lease-fixture", cancellation.Token); }, "distributed lock contention");
        }
        await using (await locked.Acquire("lease-fixture", default)) Count++;
        await using (var db = factory.CreateContext())
        {
            var protectedRow = await db.StoreSettings.SingleAsync(s => s.StoreId == store.Id && s.Name.EndsWith(connectionId));
            Check(!protectedRow.Value.Contains(Connection().ApiKey) && !protectedRow.Value.Contains(Connection().IpnSecret), "no plaintext credentials in store settings");
        }
        await RejectAsync(() => conn.Get("another-store", connectionId), "cross-store connection access");

        var notificationInvoice = await NewInvoice(); fake.Status = "new"; fake.AmountStatus = "none";
        var linked = await bridge.Synchronize(notificationInvoice.Id, true, default);
        var payload = fake.Response(notificationInvoice.Id, linked.InvoiceId!)["data"]!.DeepClone() as JObject;
        payload!["event_id"] = Guid.NewGuid().ToString();
        var body = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        var now = DateTimeOffset.UtcNow;
        await RejectAsync(() => bridge.Callback(notificationInvoice.Id, body, Sign(body, "wrong-secret", now), (string)payload["event_id"]!, default), "forged IPN");
        fake.Status = "settled"; fake.AmountStatus = "paid";
        await bridge.Callback(notificationInvoice.Id, body, Sign(body, Connection().IpnSecret, now), (string)payload["event_id"]!, default);
        Check((await repository.GetInvoice(notificationInvoice.Id)).GetPayments(false).Single().Status == PaymentStatus.Settled, "IPN uses latest API state not payload status");
        await bridge.Callback(notificationInvoice.Id, body, Sign(body, Connection().IpnSecret, now), (string)payload["event_id"]!, default);
        Check((await repository.GetInvoice(notificationInvoice.Id)).GetPayments(false).Count() == 1, "replayed signed IPN idempotent");
        Check((await repository.GetMonitoredInvoices(handler.PaymentMethodId, true)).Length > 0, "BTCPay monitoring query compatibility");
        await AwaitStatus(notificationInvoice.Id, InvoiceStatus.Settled);
        await watcher.StopAsync(default);
        aggregator.Dispose(); cache.Dispose();
    }

    sealed class FakeClient : IWhollyClient
    {
        public int Calls, Creates; public string? LastBody, LastKey;
        public bool LoseNext, WrongAmount;
        public string Status = "new", AmountStatus = "none", Timing = "on_time", Resolution = "automatic";
        readonly Dictionary<string, (string Id, string Order)> requests = new();
        public Task<JObject> Request(Connection connection, string path, string? body, string? requestId, CancellationToken ct)
        {
            Calls++;
            (string Id, string Order) item;
            if (body is not null)
            {
                LastBody = body; LastKey = requestId;
                if (!requests.TryGetValue(requestId!, out item))
                {
                    Creates++; item = (Guid.NewGuid().ToString(), ((string)JObject.Parse(body)["order_id"]!)[7..]); requests[requestId!] = item;
                }
            }
            else item = requests.Values.Single(x => path.EndsWith(x.Id));
            if (LoseNext) { LoseNext = false; throw new IOException("synthetic lost response"); }
            return Task.FromResult(Response(item.Order, item.Id));
        }
        public JObject Response(string order, string id)
        {
            var data = Remote(order); data["invoice_id"] = id; data["status"] = Status; data["amount_status"] = AmountStatus;
            data["timing_status"] = Timing; data["resolution"] = Resolution; data["sequence"] = Calls;
            if (WrongAmount) data["amount"] = "2500";
            return new JObject { ["data"] = data, ["links"] = new JObject { ["checkout"] = "https://pay.example.com/invoice/" + id } };
        }
    }
}
