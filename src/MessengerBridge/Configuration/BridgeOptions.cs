namespace MessengerBridge.Configuration;

/// <summary>
/// Настройки двух мессенджеров, локального хранилища и режима работы моста.
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>
    /// Настройки подключения к Telegram и идентификатор исходной группы.
    /// </summary>
    public EndpointOptions Telegram { get; set; } = new();
    /// <summary>
    /// Настройки подключения к MAX и идентификатор исходной группы.
    /// </summary>
    public EndpointOptions Max { get; set; } = new();
    /// <summary>
    /// Путь к SQLite; относительный путь разрешается от каталога сборки приложения.
    /// </summary>
    public string DatabasePath { get; set; } = "data/bridge.db";
    /// <summary>
    /// Тайм-аут ожидания событий long polling в секундах, от 1 до 60.
    /// </summary>
    public int PollTimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// Начальный marker MAX при отсутствии сохранённого курсора; null оставляет выбор API.
    /// </summary>
    public long? InitialMaxMarker { get; set; }
    // Conservative group flood limit; each destination has its own sender.
    /// <summary>
    /// Минимальная пауза между отправками в одном направлении, не менее 3100 миллисекунд.
    /// </summary>
    public int SendIntervalMilliseconds { get; set; } = 3100;

    /// <summary>
    /// Проверяет допустимость общих настроек и параметров обоих мессенджеров.
    /// </summary>
    /// <param name="requireTokens">Нужно ли проверять наличие и формат токенов ботов.</param>
    /// <exception cref="InvalidOperationException">Настройки содержат недопустимое значение или не содержат обязательного параметра.</exception>
    public void Validate(bool requireTokens)
    {
        if (PollTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("Bridge:PollTimeoutSeconds должен быть в диапазоне от 1 до 60.");
        }

        if (SendIntervalMilliseconds < 3100)
        {
            throw new InvalidOperationException("Bridge:SendIntervalMilliseconds должен быть не меньше 3100.");
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("Необходимо задать Bridge:DatabasePath.");
        }

        if (InitialMaxMarker < 0)
        {
            throw new InvalidOperationException("Bridge:InitialMaxMarker должен быть неотрицательным числом или null.");
        }

        ValidateEndpoint(Telegram, "Telegram", requireTokens);
        ValidateEndpoint(Max, "Max", requireTokens);
    }

    /// <summary>
    /// Проверяет чат, адрес прокси и при необходимости токен одного мессенджера.
    /// </summary>
    /// <param name="options">Проверяемые настройки подключения.</param>
    /// <param name="name">Имя раздела конфигурации: Telegram или Max.</param>
    /// <param name="requireToken">Нужно ли проверять токен бота.</param>
    /// <exception cref="InvalidOperationException">Идентификатор чата, адрес прокси или обязательный токен некорректны.</exception>
    private static void ValidateEndpoint(EndpointOptions options, string name, bool requireToken)
    {
        if (options.ChatId == 0)
        {
            throw new InvalidOperationException($"Необходимо задать Bridge:{name}:ChatId — 64-разрядный идентификатор чата (Int64).");
        }

        if (requireToken && (string.IsNullOrWhiteSpace(options.Token) || options.Token.Any(char.IsWhiteSpace)))
        {
            throw new InvalidOperationException($"Задайте Bridge:{name}:Token через User Secrets или переменные окружения.");
        }

        if (name == "Telegram" && requireToken &&
            !System.Text.RegularExpressions.Regex.IsMatch(options.Token, @"^\d+:[A-Za-z0-9_-]+$"))
        {
            throw new InvalidOperationException("Bridge:Telegram:Token имеет неверный формат.");
        }

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl) &&
            (!Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out Uri? uri) ||
             uri.Scheme is not ("http" or "https" or "socks5")))
        {
            throw new InvalidOperationException($"Bridge:{name}:ProxyUrl должен быть адресом с протоколом http, https или socks5.");
        }
    }
}