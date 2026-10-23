namespace MessengerBridge.Clients;

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