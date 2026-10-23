using System.Net;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

public static class ApiTransport
{
    public static HttpClient Create(EndpointOptions options, Uri baseAddress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Deliberate: ignore environment/system proxies when ProxyUrl is blank.
            UseProxy = !string.IsNullOrWhiteSpace(options.ProxyUrl)
        };
        if (handler.UseProxy) handler.Proxy = new WebProxy(options.ProxyUrl);
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(90) };
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                if (retry is null && response.Headers.RetryAfter?.Date is { } date)
                    retry = Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
                int? seconds = retry is { } value ? (int)Math.Ceiling(Math.Min(value, int.MaxValue)) : null;
                long? migrate = null;
                try
                {
                    using var error = JsonDocument.Parse(json);
                    if (error.RootElement.TryGetProperty("parameters", out JsonElement parameters))
                    {
                        if (parameters.TryGetProperty("retry_after", out JsonElement after)) seconds = after.GetInt32();
                        if (parameters.TryGetProperty("migrate_to_chat_id", out JsonElement id)) migrate = id.GetInt64();
                    }
                }
                catch (JsonException) { }
                throw new ApiException((int)response.StatusCode, seconds, migrate);
            }
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}