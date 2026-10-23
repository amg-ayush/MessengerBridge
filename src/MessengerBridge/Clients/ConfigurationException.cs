namespace MessengerBridge.Clients;

/// <summary>
/// Ошибка настройки бота или группы, требующая исправления конфигурации и перезапуска.
/// </summary>
/// <param name="message">Описание причины без секретов и содержимого переписки.</param>
public sealed class ConfigurationException(string message) : Exception(message);