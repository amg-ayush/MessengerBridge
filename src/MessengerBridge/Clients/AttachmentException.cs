namespace MessengerBridge.Clients;

/// <summary>
/// Постоянная ошибка передачи вложения, не требующая остановки остальных сообщений.
/// </summary>
/// <param name="message">Безопасное русскоязычное описание ограничения.</param>
public sealed class AttachmentException(string message) : Exception(message);