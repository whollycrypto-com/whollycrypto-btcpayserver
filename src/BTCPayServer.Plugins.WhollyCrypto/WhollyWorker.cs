using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.WhollyCrypto;

public sealed class WhollyWorker(InvoiceRepository invoices, WhollyBridge bridge, ILogger<WhollyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var monitored = await invoices.GetMonitoredInvoices(WhollyPaymentHandler.Method, true, stoppingToken);
                foreach (var item in monitored.Select(i => (Invoice: i, Details: WhollyBridge.Details(i)))
                    .Where(x => x.Details?.RequestJson is not null && (x.Details.NextCheck is null || x.Details.NextCheck <= DateTimeOffset.UtcNow))
                    .OrderBy(x => x.Details!.NextCheck).Take(20))
                {
                    try { await bridge.Synchronize(item.Invoice.Id, false, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch { /* Sanitized diagnostics and Retry-After are retained on the invoice. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Wholly Crypto reconciliation is temporarily unavailable; it will retry."); }
        }
    }
}
