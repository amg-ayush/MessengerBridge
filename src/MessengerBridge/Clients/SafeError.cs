namespace MessengerBridge.Clients;

/// <summary>
/// Формирует безопасные описания ошибок и задержки повторных попыток.
/// </summary>
public static class SafeError
{
    /// <summary>
    /// Описывает ошибку без URL запросов, токенов и содержимого сообщений.
    /// </summary>
    /// <param name="exception">Ошибка, которую нужно записать в журнал или очередь.</param>
    /// <returns>Безопасное краткое описание ошибки.</returns>
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

    /// <summary>
    /// Вычисляет экспоненциальную задержку со случайной добавкой и учётом требования сервера.
    /// </summary>
    /// <param name="attempts">Номер неудачной попытки, начиная с 1.</param>
    /// <param name="retryAfter">Минимальная задержка сервера в секундах либо null.</param>
    /// <returns>Максимум из задержки сервера и экспоненциальной задержки со случайной добавкой менее секунды.</returns>
    /// <remarks>Экспоненциальная составляющая ограничивается показателем 8, то есть 256 секундами; требование сервера может быть больше.</remarks>
    public static TimeSpan Backoff(int attempts, int? retryAfter = null) =>
        TimeSpan.FromSeconds(Math.Max(retryAfter ?? 0,
            Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))) + Random.Shared.NextDouble()));
}