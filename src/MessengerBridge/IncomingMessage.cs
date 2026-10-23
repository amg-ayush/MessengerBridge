namespace MessengerBridge;

/// <summary>
/// Сообщение пользователя с текстом, подписью и файлами для постановки в очередь.
/// </summary>
/// <param name="SourceId">Идентификатор сообщения на исходной платформе.</param>
/// <param name="SourceChatId">Идентификатор исходного чата.</param>
/// <param name="Author">Отображаемое имя автора.</param>
/// <param name="Text">Текст или подпись без заголовка моста.</param>
/// <param name="Attachments">Медиафайлы сообщения; null для обычного текста.</param>
public sealed record IncomingMessage(string SourceId, long SourceChatId, string Author, string Text,
    IReadOnlyList<MessageAttachment>? Attachments = null);