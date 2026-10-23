namespace MessengerBridge;

/// <summary>
/// Общий контракт проверки, получения и отправки сообщений мессенджера.
/// </summary>
public interface IMessengerClient
{
    /// <summary>
    /// Платформа, обслуживаемая клиентом.
    /// </summary>
    Platform Platform { get; }
    /// <summary>
    /// Проверяет доступ к боту и группе, необходимые права и отсутствие активного Webhook.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Задача, представляющая выполнение операции.</returns>
    /// <exception cref="Clients.ConfigurationException">Настройки группы, права бота или режим получения событий несовместимы с мостом.</exception>
    Task VerifyAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Читает очередную порцию событий и отбирает сообщения для пересылки.
    /// </summary>
    /// <param name="cursor">Курсор чтения либо null для начального запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Сообщения для очереди и курсор следующего запроса.</returns>
    Task<PollBatch> PollAsync(long? cursor, CancellationToken cancellationToken);
    /// <summary>
    /// Отправляет текстовое сообщение от имени бота.
    /// </summary>
    /// <param name="chatId">Идентификатор целевого группового чата.</param>
    /// <param name="text">Текст сообщения без HTML- или Markdown-разметки.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Идентификатор отправленного сообщения на целевой платформе.</returns>
    Task<string> SendAsync(long chatId, string text, CancellationToken cancellationToken);
}