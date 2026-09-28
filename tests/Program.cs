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
using BTCPayServer.Plugins.PluginManagement.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var tests = new Checks();
if (args.Length == 2 && args[0] == "--seed-embedded-browser") { await tests.SeedEmbeddedBrowser(args[1]); return; }
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
        Check(new Plugin().Version.ToString() == "1.0.1", "plugin version excludes assembly revision or source hash");
        var hostDependencies = new Plugin().Dependencies.Where(d => d.Identifier == "BTCPayServer").ToArray();
        Check(hostDependencies.Length == 1, "one unambiguous BTCPay dependency");
        Check(hostDependencies[0].Condition == ">=2.4.4", "builder-compatible minimum version without an upper bound");
        Check(!JsonConvert.DeserializeObject<Connection>("{}")!.EmbedCheckout, "old connections retain full-page mode");
        var cards = new InstalledPluginsViewModel { InstalledPlugins = [
            new() { Current = new() { Identifier = new Plugin().Identifier } },
            new() { Current = new() { Identifier = "Unrelated.Plugin", Documentation = "https://example.com/other" } }] };
        PluginResourcesFilter.Decorate(cards);
        Check(cards.InstalledPlugins[0].Current.Documentation == PluginResourcesFilter.Repository + "#setup", "manual-upload card has docs");
        Check(cards.InstalledPlugins[1].Current.Documentation == "https://example.com/other", "other plugins unchanged");
        var browsing = new InvoiceEntity { Status = InvoiceStatus.New };
        var progress = Prompt(); progress.RemoteStatus = "processing";
        Check(!WhollyCryptoController.ShouldReturn(browsing, progress), "processing keeps iframe open");
        progress.RemoteStatus = "settled";
        Check(WhollyCryptoController.ShouldReturn(browsing, progress), "verified settlement returns to BTCPay");
        progress.RemoteStatus = "new"; progress.Review = "Review required";
        Check(WhollyCryptoController.ShouldReturn(browsing, progress), "review exits payment frame");
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
        var catalog = PaymentSelection.Catalog(Catalog());
        Check(catalog.Count == 2 && catalog.Any(a => a.Lightning), "catalog includes accepted onchain and Lightning only");
        var chosen = PaymentSelection.Select([AssetId], catalog);
        Check(chosen.Single().Symbol == "USDC", "asset selection resolves store identity");
        Reject(() => PaymentSelection.Select([], catalog), "empty subset");
        Reject(() => PaymentSelection.Select([Guid.NewGuid().ToString()], catalog), "unaccepted asset");
        Reject(() => PaymentSelection.Select(["bitcoin:lightning"], catalog.Select(a => a with { Ready = false }).ToList()), "unready method");
        var selection = PaymentSelection.Request(catalog);
        Check(selection.Count == 2 && (string?)selection[0]["asset_ids"]?[0] == AssetId, "exact onchain UUID selection");
        Check((string?)selection[1]["payment_rail"] == "lightning", "Lightning explicitly separate");
        var restricted = Connection(); restricted.AcceptedMethods = chosen;
        var selectedInvoice = Remote(); selectedInvoice["payment_intents"] = Intents();
        PaymentSelection.VerifyInvoice(restricted, selectedInvoice); Count++;
        selectedInvoice["payment_intents"]![0]!["asset_id"] = Guid.NewGuid().ToString();
        Reject(() => PaymentSelection.VerifyInvoice(restricted, selectedInvoice), "API fallback cannot broaden asset selection");
        Reject(() => PaymentSelection.VerifyInvoice(restricted, Remote()), "missing method evidence");
        var observation = Remote(); observation["payment_intents"] = Intents();
        Check(WhollyBridge.ObservedAsset(observation).Asset is null, "empty methods do not imply a received asset");
        observation["payment_intents"]![0]!["received_amount"] = "1.00000100";
        Check(WhollyBridge.ObservedAsset(observation) == ("ethereum", "USDC", "1.000001"), "received metadata derives from actual invoice GET contract");
        ((JArray)observation["payment_intents"]!).Add(new JObject { ["id"] = "second-method", ["received_amount"] = "1", ["symbol"] = "OTHER", ["chain_slug"] = "solana" });
        Check(WhollyBridge.ObservedAsset(observation).Asset is null, "multiple received assets are not guessed");
        observation["winning_payment_intent_id"] = AssetId;
        Check(WhollyBridge.ObservedAsset(observation).Asset == "USDC", "explicit API winning method selects settlement display");
        p.LastCallback = now; p.CallbackInvoiceId = id; p.CallbackPending = true; p.CallbackEvents.Add(Guid.NewGuid().ToString());
        new WhollyPaymentHandler(null!, null!).StripDetailsForNonOwner(p);
        Check(p.LastCallback is null && p.CallbackInvoiceId is null && !p.CallbackPending && p.CallbackEvents.Count == 0, "public prompt strips callback diagnostics");
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
        var activity = new ActivityRepository(factory);
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
        Check(JObject.Parse(request!)["redirect_automatically"]!.Value<bool>(), "full-page mode preserves automatic return");
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
        Check(WhollyBridge.Details(done)!.PaidAsset == "USDC" && WhollyBridge.Details(done)!.PaidAmount == "28.73", "paid asset is retained from verified API intents");
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
        var callsBeforeCallback = fake.Calls;
        await bridge.Callback(notificationInvoice.Id, body, Sign(body, Connection().IpnSecret, now), (string)payload["event_id"]!, default);
        Check(fake.Calls == callsBeforeCallback, "signed callback acknowledges without a remote HTTP roundtrip");
        Check((await repository.GetInvoice(notificationInvoice.Id)).GetPayments(false).Count() == 0, "IPN receipt is not payment proof");
        Check((await activity.PendingCallbacks(default)).Contains(notificationInvoice.Id), "callback job durable and visible to restarted worker");
        await restarted.Synchronize(notificationInvoice.Id, false, default);
        Check((await repository.GetInvoice(notificationInvoice.Id)).GetPayments(false).Single().Status == PaymentStatus.Settled, "IPN uses latest API state not payload status");
        await bridge.Callback(notificationInvoice.Id, body, Sign(body, Connection().IpnSecret, now), (string)payload["event_id"]!, default);
        Check((await repository.GetInvoice(notificationInvoice.Id)).GetPayments(false).Count() == 1, "replayed signed IPN idempotent");
        Check(!(await activity.PendingCallbacks(default)).Contains(notificationInvoice.Id), "identical callback does not requeue verified event");
        Check((await repository.GetMonitoredInvoices(handler.PaymentMethodId, true)).Length > 0, "BTCPay monitoring query compatibility");
        await AwaitStatus(notificationInvoice.Id, InvoiceStatus.Settled);

        // A new connection version changes presentation only for new invoices.
        var embeddedConnection = Connection(); embeddedConnection.EmbedCheckout = true;
        var oldId = connectionId;
        connectionId = await conn.Save(store.Id, embeddedConnection);
        store.SetPaymentMethodConfig(handler, new MethodConfig { ConnectionId = connectionId });
        await storeRepository.UpdateStore(store);
        Check(!(await conn.Get(store.Id, oldId)).EmbedCheckout && (await conn.Get(store.Id, connectionId)).EmbedCheckout,
            "embedded preference is versioned; old invoices retain full page");
        var embeddedInvoice = await NewInvoice(); fake.Status = "new"; fake.AmountStatus = "none";
        var controller = new WhollyCryptoController(storeRepository, conn, fake, handler, repository, bridge, activity) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var start = await controller.Start(embeddedInvoice.Id, default) as RedirectToActionResult;
        Check(start?.ActionName == "Embedded", "POST starts embedded mode through local PRG redirect");
        var embeddedRequest = JObject.Parse(fake.LastBody!);
        Check(embeddedRequest["redirect_automatically"]!.Value<bool>() == false, "iframe disables in-frame automatic navigation");
        Check((string?)embeddedRequest["redirect_url"] == "https://btcpay.example.com/plugins/whollycrypto/return/" + embeddedInvoice.Id,
            "iframe return is controlled by same-origin bridge");
        var calls = fake.Calls;
        Check(await controller.Status(embeddedInvoice.Id) is JsonResult, "browser status endpoint returns local state");
        Check(fake.Calls == calls, "browser status requests cannot trigger remote API load");
        Check(await controller.Status("not-valid") is NotFoundResult, "bad invoice identifiers rejected");

        // Callback arrives before a lost creation response, even after local expiry.
        var recovery = await NewInvoice(); fake.LoseNext = true;
        await RejectAsync(() => bridge.Synchronize(recovery.Id, true, default), "creation response lost before IPN");
        var remoteId = fake.LastCreatedId!;
        var recoveryPayload = (JObject)fake.Response(recovery.Id, remoteId)["data"]!;
        recoveryPayload["event_id"] = Guid.NewGuid().ToString(); recoveryPayload["event_type"] = "invoice.expired";
        var recoveryBody = Encoding.UTF8.GetBytes(recoveryPayload.ToString(Formatting.None));
        var recoveryCalls = fake.Calls;
        await bridge.Callback(recovery.Id, recoveryBody, Sign(recoveryBody, Connection().IpnSecret, now), (string)recoveryPayload["event_id"]!, default);
        Check(fake.Calls == recoveryCalls && WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!.InvoiceId is null,
            "early IPN only records lookup candidate, not an authoritative invoice");
        var recoveredQueue = WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!;
        recoveredQueue.NextCheck = DateTimeOffset.UtcNow.AddSeconds(-1); // Advance only the synthetic retry deadline.
        await repository.UpdatePaymentDetails(recovery.Id, handler, recoveredQueue);
        await using (var db = factory.CreateContext())
        {
            var data = await db.Invoices.FindAsync(recovery.Id) ?? throw new Exception("Missing synthetic recovery invoice");
            var entity = data.GetBlob(); entity.ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            data.SetBlob(entity); data.Status = "Expired"; await db.SaveChangesAsync();
        }
        Check((await activity.PendingCallbacks(default)).Contains(recovery.Id), "expired invoices remain in durable callback queue");
        fake.Status = "expired"; fake.AmountStatus = "none";
        var createsBeforeRecovery = fake.Creates;
        await restarted.Synchronize(recovery.Id, false, default);
        Check(fake.Creates == createsBeforeRecovery && WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!.InvoiceId == remoteId,
            "lost response recovery uses authenticated GET, never recreates expired invoice");

        // Backoff survives duplicate/new notifications and restart.
        recoveryPayload["event_id"] = Guid.NewGuid().ToString();
        recoveryBody = Encoding.UTF8.GetBytes(recoveryPayload.ToString(Formatting.None));
        await bridge.Callback(recovery.Id, recoveryBody, Sign(recoveryBody, Connection().IpnSecret, now), (string)recoveryPayload["event_id"]!, default);
        fake.RateLimited = true;
        await RejectAsync(() => restarted.Synchronize(recovery.Id, false, default), "API rate limit while queued");
        var waiting = WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!;
        Check(waiting.CallbackPending && waiting.NextCheck > DateTimeOffset.UtcNow.AddMinutes(4), "callback job and Retry-After survive failure");
        recoveryPayload["event_id"] = Guid.NewGuid().ToString(); recoveryBody = Encoding.UTF8.GetBytes(recoveryPayload.ToString(Formatting.None));
        await bridge.Callback(recovery.Id, recoveryBody, Sign(recoveryBody, Connection().IpnSecret, now), (string)recoveryPayload["event_id"]!, default);
        Check(!(await activity.PendingCallbacks(default)).Contains(recovery.Id), "new IPN cannot bypass rate-limit backoff");
        fake.RateLimited = false;
        waiting = WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!;
        waiting.NextCheck = DateTimeOffset.UtcNow.AddSeconds(-1); // Synthetic time advance after backoff.
        await repository.UpdatePaymentDetails(recovery.Id, handler, waiting);
        using (var worker = new WhollyWorker(repository, restarted, activity, NullLogger<WhollyWorker>.Instance))
        {
            await worker.StartAsync(default);
            for (var attempt = 0; attempt < 500 && WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!.CallbackPending; attempt++)
                await Task.Delay(50);
            await worker.StopAsync(default);
        }
        Check(!WhollyBridge.Details(await repository.GetInvoice(recovery.Id))!.CallbackPending, "actual background worker drains expired callback after restart/backoff");

        var health = await activity.Health(store.Id, oldId, default);
        Check(health.LastWrite is not null && health.LastCheck is not null && health.LastCallback is not null && health.CallbackVerifiedAt is not null,
            "health separates creation, reads, callback receipt and verification");
        var list = await activity.List(store.Id, linked.InvoiceId, "all", 1, default);
        Check(list.Total == 1 && list.Rows.Single().Id == notificationInvoice.Id, "Wholly invoice ID search");
        Check((await activity.List(store.Id, "synthetic-order", "review", 1, default)).Total >= 3, "order search plus review filter");
        Check((await activity.List("unrelated-store", linked.InvoiceId, "all", 1, default)).Total == 0, "activity isolates BTCPay stores");
        Check((await activity.Health("unrelated-store", oldId, default)).LastCheck is null, "health isolates stores");
        Check((await activity.List(store.Id, "%' OR true --", "all", 1, default)).Total == 0, "search is parameterized literal text");
        var healthRecord = new ConnectionHealth { CheckedAt = DateTimeOffset.UtcNow, Assets = PaymentSelection.Catalog(Catalog()) };
        await conn.SaveHealth(store.Id, connectionId, healthRecord);
        Check((await conn.Health(store.Id, connectionId))!.Assets.Count == 2, "catalogue/read health persists per connection");

        // Immutable restricted selection and merchant fallback protection.
        var selectionConnection = Connection(); selectionConnection.AcceptedMethods = PaymentSelection.Select([AssetId], PaymentSelection.Catalog(Catalog()));
        connectionId = await conn.Save(store.Id, selectionConnection);
        store.SetPaymentMethodConfig(handler, new MethodConfig { ConnectionId = connectionId }); await storeRepository.UpdateStore(store);
        var selected = await NewInvoice(); fake.Status = "new"; fake.AmountStatus = "none";
        await bridge.Synchronize(selected.Id, true, default);
        Check((string?)JObject.Parse(fake.LastBody!)["payment_methods"]?[0]?["asset_ids"]?[0] == AssetId, "creation uses selected store asset UUID");
        var broadened = await NewInvoice(); fake.BroadenMethods = true;
        await RejectAsync(() => bridge.Synchronize(broadened.Id, true, default), "store policy race/fallback");
        var blocked = WhollyBridge.Details(await repository.GetInvoice(broadened.Id))!;
        Check(blocked.InvoiceId is not null && blocked.CheckoutUrl is null && blocked.Error is not null,
            "failed method proof retains remote ID but withholds checkout");
        var countCreated = fake.Creates;
        await RejectAsync(() => bridge.Synchronize(broadened.Id, true, default), "retry policy mismatch");
        Check(fake.Creates == countCreated, "rejected broad checkout cannot create duplicate invoices");
        fake.BroadenMethods = false;
        Check((await conn.Get(store.Id, oldId)).AcceptedMethods is null, "old connection continues inherited payment selection");

        // Actual paging boundaries, deterministic sort and clamping.
        for (var n = 0; n < 22; n++)
        {
            var pageInvoice = await NewInvoice();
            var pp = WhollyBridge.Details(pageInvoice)!; pp.RequestJson = "{}";
            await repository.UpdatePaymentDetails(pageInvoice.Id, handler, pp);
        }
        var firstPage = await activity.List(store.Id, "", "all", 1, default);
        var secondPage = await activity.List(store.Id, "", "all", 2, default);
        Check(firstPage.Rows.Count == 20 && firstPage.Pages > 1 && secondPage.Rows.Count > 0, "20-row server-side paging");
        Check(!firstPage.Rows.Select(x => x.Id).Intersect(secondPage.Rows.Select(x => x.Id)).Any(), "pages do not overlap");
        Check((await activity.List(store.Id, "", "all", int.MaxValue, default)).Page == firstPage.Pages, "excess page safely clamped");
        controller.HttpContext.SetStoreData(new BTCPayServer.Data.StoreData { Id = "unrelated-store" });
        Check(await controller.Payments(store.Id, null, null) is NotFoundResult, "list requires matching authorized store context");
        Check(await controller.Refresh("unrelated-store", notificationInvoice.Id, default) is NotFoundResult, "cannot verify another store's invoice");
        await watcher.StopAsync(default);
        aggregator.Dispose(); cache.Dispose();
    }

    public async Task SeedEmbeddedBrowser(string invoiceId)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WHOLLY_TEST_DATABASE"));
        if (settings.Database is null || !settings.Database.StartsWith("wholly_btcpay_test_", StringComparison.Ordinal))
            throw new Exception("Refusing non-test database.");
        var factory = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = settings.ConnectionString }), NullLoggerFactory.Instance);
        var aggregator = new EventAggregator(new Logs());
        var repository = new InvoiceRepository(factory, aggregator);
        var invoice = await repository.GetInvoice(invoiceId);
        if (invoice?.Metadata.OrderId != "synthetic-browser-order") throw new Exception("Only synthetic browser fixture invoices allowed.");
        var prompt = invoice.GetPaymentPrompt(WhollyPaymentHandler.Method)!;
        var details = prompt.Details.ToObject<PromptDetails>()!;
        details.InvoiceId = "33333333-3333-4333-8333-333333333333";
        details.CheckoutUrl = "https://pay.example.com/invoice/" + details.InvoiceId;
        details.RemoteStatus = "new";
        details.RequestJson = "{}"; details.NextCheck = DateTimeOffset.UtcNow.AddDays(1);
        await repository.UpdatePaymentDetails(invoice.Id, new WhollyPaymentHandler(null!, null!), details);
        // Rich operator UI fixtures; no real connection or remote callback required.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var storeRepository = new StoreRepository(factory, new JsonSerializerSettings(), aggregator, new SettingsRepository(factory, aggregator, cache));
        var store = await storeRepository.FindStore(invoice.StoreId) ?? throw new Exception("Missing synthetic store");
        await storeRepository.UpdateSetting(store.Id, "WhollyCrypto.Health." + details.ConnectionId,
            new ConnectionHealth { CheckedAt = DateTimeOffset.UtcNow, Assets = PaymentSelection.Catalog(Catalog()) });
        var handler = new WhollyPaymentHandler(null!, null!);
        for (var n = 0; n < 24; n++)
        {
            var item = repository.CreateNewInvoice(store.Id); item.Price = 25; item.Currency = "EUR"; item.Status = InvoiceStatus.New;
            item.ServerUrl = "https://btcpay.example.com/"; item.ExpirationTime = DateTimeOffset.UtcNow.AddHours(1);
            item.MonitoringExpiration = DateTimeOffset.UtcNow.AddDays(1); item.Metadata.OrderId = "synthetic-list-order-" + n;
            var pd = new PromptDetails { ConnectionId = details.ConnectionId, Amount = 25, Currency = "EUR", RequestJson = "{}",
                InvoiceId = Guid.NewGuid().ToString(), NextCheck = DateTimeOffset.UtcNow.AddDays(1), RemoteStatus = n % 2 == 0 ? "settled" : "new",
                PaidAsset = n % 2 == 0 ? "USDC" : null, PaidChain = n % 2 == 0 ? "ethereum" : null, PaidAmount = n % 2 == 0 ? "28.73" : null,
                Error = n == 2 ? "Wholly API HTTP 429: wait for the retry deadline." : null,
                Review = n == 3 ? "Synthetic late payment. Verify before fulfillment." : null,
                LastCallback = DateTimeOffset.UtcNow, LastCallbackType = "invoice.settled", LastCheck = DateTimeOffset.UtcNow,
                CreatedViaApiAt = DateTimeOffset.UtcNow, CallbackVerifiedAt = DateTimeOffset.UtcNow };
            prompt.Details = JObject.FromObject(pd); item.SetPaymentPrompt(handler.PaymentMethodId, prompt);
            await repository.CreateInvoiceAsync(new InvoiceCreationContext(store, store.GetStoreBlob(), item, new InvoiceLogs(), new PaymentMethodHandlerDictionary([handler]), null));
        }
        aggregator.Dispose();
        Console.WriteLine("Synthetic embedded browser fixture prepared; no remote requests or payments.");
    }

    const string AssetId = "44444444-4444-4444-8444-444444444444";
    static JArray Intents() => [new JObject { ["id"] = AssetId, ["asset_id"] = AssetId, ["chain_slug"] = "ethereum", ["symbol"] = "USDC", ["payment_rail"] = "onchain", ["received_amount"] = "0" }];
    static JObject Catalog() => new() {
        ["data"] = new JArray(new JObject { ["selected"] = true, ["wallet_readiness"] = "ready", ["asset"] = new JObject {
            ["id"] = AssetId, ["chain_slug"] = "ethereum", ["symbol"] = "USDC", ["name"] = "USDC", ["contract_address"] = "0x0000000000000000000000000000000000000001" } },
            new JObject { ["selected"] = false, ["asset"] = new JObject { ["symbol"] = "UNACCEPTED" } }),
        ["lightning"] = new JObject { ["enabled"] = true, ["ready"] = true }
    };

    sealed class FakeClient : IWhollyClient
    {
        public int Calls, Creates; public string? LastBody, LastKey;
        public bool LoseNext, WrongAmount, RateLimited, BroadenMethods;
        public string? LastCreatedId;
        public string Status = "new", AmountStatus = "none", Timing = "on_time", Resolution = "automatic";
        readonly Dictionary<string, (string Id, string Order)> requests = new();
        public Task<JObject> Request(Connection connection, string path, string? body, string? requestId, CancellationToken ct)
        {
            Calls++;
            if (path.EndsWith("/payment-assets")) return Task.FromResult(Catalog());
            if (RateLimited) throw new ConnectorException("Wholly API HTTP 429: fixture", 300);
            (string Id, string Order) item;
            if (body is not null)
            {
                LastBody = body; LastKey = requestId;
                if (!requests.TryGetValue(requestId!, out item))
                {
                    Creates++; item = (Guid.NewGuid().ToString(), ((string)JObject.Parse(body)["order_id"]!)[7..]); requests[requestId!] = item;
                }
                LastCreatedId = item.Id;
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
            data["payment_intents"] = Intents();
            if (BroadenMethods) data["payment_intents"]![0]!["asset_id"] = "55555555-5555-4555-8555-555555555555";
            if (Status == "settled") { data["winning_payment_intent_id"] = AssetId; data["payment_intents"]![0]!["received_amount"] = "28.73"; }
            return new JObject { ["data"] = data, ["links"] = new JObject { ["checkout"] = "https://pay.example.com/invoice/" + id } };
        }
    }
}
