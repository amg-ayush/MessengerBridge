namespace MessengerBridge;

/// <summary>
/// Часть сообщения из очереди, ожидающая отправки в другой мессенджер.
/// </summary>
/// <param name="Id">Идентификатор записи очереди.</param>
/// <param name="Source">Платформа, из которой получено сообщение.</param>
/// <param name="DestinationChatId">Идентификатор целевого группового чата.</param>
/// <param name="Text">Готовый к отправке текст с заголовком автора.</param>
/// <param name="Attempts">Количество зарегистрированных неудачных попыток отправки.</param>
/// <param name="NextAttemptAt">Самое раннее время повторной отправки в миллисекундах Unix; 0 разрешает отправку сразу.</param>
public sealed record OutboxItem(long Id, Platform Source, long DestinationChatId, string Text,
    int Attempts, long NextAttemptAt);