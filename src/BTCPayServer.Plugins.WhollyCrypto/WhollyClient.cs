using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.WhollyCrypto;

public interface IWhollyClient
{
    Task<JObject> Request(Connection connection, string path, string? body, string? requestId, CancellationToken ct);
}

public sealed class WhollyClient : IWhollyClient
{
    public async Task<JObject> Request(Connection c, string path, string? body, string? requestId, CancellationToken ct)
    {
        var origin = Protocol.Origin(c.ApiOrigin);
        if (!path.StartsWith("/v1/projects/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('?'))
            throw new ConnectorException("Unexpected API path.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (ctx, token) =>
            {
                if (ctx.DnsEndPoint.Host != origin.IdnHost || ctx.DnsEndPoint.Port != 443)
                    throw new ConnectorException("Unexpected outbound API destination.");
                var addresses = await Dns.GetHostAddressesAsync(origin.IdnHost, token);
                if (addresses.Length == 0 || addresses.Any(a => !Protocol.IsPublic(a)))
                    throw new ConnectorException("The API hostname must resolve only to public addresses.");
                // Connect to an already-validated address, not a second DNS lookup.
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(address, 443, token); return new NetworkStream(socket, true); }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new ConnectorException("Could not connect to the Wholly API.");
            }
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(origin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("WhollyCrypto-BTCPay/1.0.1");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("Idempotency-Key", requestId);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            var wait = (int)Math.Clamp(delay?.TotalSeconds ?? 60, 30, 3600);
            var help = (int)response.StatusCode switch
            {
                401 => "API authentication failed. Check the saved credential and any proxy login challenge.",
                403 => "Access denied. Check project scope, read/write permission, IP restrictions and proxy rules.",
                404 => "Project, store or invoice not found. Verify the API IDs and domain.",
                429 => "API rate limit reached. Verification will retry after the indicated delay.",
                400 or 422 => "The invoice request was rejected. Check currency, accepted assets, wallets and rates in Wholly.",
                409 => "Request conflict. Do not create another invoice; review the saved request in both systems.",
                >= 300 and < 400 => "An API redirect was refused. Use the final HTTPS API origin without a login redirect.",
                _ => "The API is unavailable. Verification will retry; do not ask the customer to pay again."
            };
            throw new ConnectorException($"Wholly API HTTP {(int)response.StatusCode}: {help}", wait);
        }
        if (response.Content.Headers.ContentLength > Protocol.MaxBody) throw new ConnectorException("API response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + read > Protocol.MaxBody) throw new ConnectorException("API response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return Protocol.Json(buffer.ToArray());
    }
}
