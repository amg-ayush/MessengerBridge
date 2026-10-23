namespace MessengerBridge;

/// <summary>
/// Результат чтения событий: сообщения для пересылки и следующий курсор.
/// </summary>
/// <param name="NextCursor">Курсор следующего запроса либо null, если API его не вернул.</param>
/// <param name="Messages">Сообщения, прошедшие фильтрацию чата, автора и содержимого.</param>
public sealed record PollBatch(long? NextCursor, IReadOnlyList<IncomingMessage> Messages);