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