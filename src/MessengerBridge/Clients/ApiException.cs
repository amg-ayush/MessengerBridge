using System.Net;

namespace MessengerBridge.Clients;

// No URL, response body, token or message content is retained in exceptions.
public sealed class ApiException(int status, int? retryAfterSeconds = null, long? migratedChatId = null)
    : Exception($"API status {status}")
{
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public long? MigratedChatId { get; } = migratedChatId;
    public bool Transient => Status is 408 or 409 or 425 or 429 || Status >= 500;
}

public sealed class ConfigurationException(string message) : Exception(message);

public static class SafeError
{
    public static string Describe(Exception exception) => exception switch
    {
        ApiException api when api.MigratedChatId is { } id =>
            $"API {api.Status}; Telegram group migrated, set ChatId={id} (see README)",
        ApiException api => $"API {api.Status}",
        ConfigurationException config => config.Message,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "TLS validation failed; verify the certificate chain (MAX: see README)",
        HttpRequestException http => $"Network: {http.HttpRequestError}",
        OperationCanceledException => "Request timed out or was cancelled",
        System.Text.Json.JsonException => "Unexpected JSON response",
        _ => exception.GetType().Name
    };

    public static TimeSpan Backoff(int attempts, int? retryAfter = null) =>
        TimeSpan.FromSeconds(Math.Max(retryAfter ?? 0,
            Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))) + Random.Shared.NextDouble()));
}
