namespace MessengerBridge.Configuration;

/// <summary>
/// Параметры доступа к боту и целевой группе одного мессенджера.
/// </summary>
public sealed class EndpointOptions
{
    /// <summary>
    /// Токен бота, загружаемый из User Secrets или переменных окружения.
    /// </summary>
    public string Token { get; set; } = "";
    /// <summary>
    /// 64-разрядный идентификатор группы, сообщения которой обслуживает мост.
    /// </summary>
    public long ChatId { get; set; }
    /// <summary>
    /// Адрес HTTP, HTTPS или SOCKS5-прокси; пустое значение включает прямое подключение.
    /// </summary>
    public string ProxyUrl { get; set; } = "";
}