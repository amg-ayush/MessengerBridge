namespace MessengerBridge;

/// <summary>
/// Текстовое сообщение пользователя, подготовленное для постановки в очередь.
/// </summary>
/// <param name="SourceId">Идентификатор сообщения на исходной платформе.</param>
/// <param name="SourceChatId">Идентификатор исходного чата.</param>
/// <param name="Author">Отображаемое имя автора.</param>
/// <param name="Text">Исходный текст без заголовка моста.</param>
public sealed record IncomingMessage(string SourceId, long SourceChatId, string Author, string Text);