namespace MessengerBridge.Clients;

// No URL, response body, token or message content is retained in exceptions.
/// <summary>
/// Ошибка API без сохранения URL, токена, тела ответа или текста сообщения.
/// </summary>
/// <param name="status">Код HTTP-ошибки или код ошибки, возвращённый API.</param>
/// <param name="retryAfterSeconds">Запрошенная сервером задержка повтора в секундах либо null.</param>
/// <param name="migratedChatId">Новый идентификатор Telegram-группы после миграции либо null.</param>
public sealed class ApiException(int status, int? retryAfterSeconds = null, long? migratedChatId = null)
    : Exception($"Ошибка API: {status}")
{
    /// <summary>
    /// Код ошибки API.
    /// </summary>
    public int Status { get; } = status;
    /// <summary>
    /// Минимальная задержка повторной попытки в секундах, указанная API.
    /// </summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    /// <summary>
    /// Новый идентификатор Telegram-группы, если API сообщил о миграции.
    /// </summary>
    public long? MigratedChatId { get; } = migratedChatId;
    /// <summary>
    /// Признак временной ошибки, для которой следует запланировать повторную попытку.
    /// </summary>
    public bool Transient => Status is 408 or 409 or 425 or 429 or >= 500;
}