using MessengerBridge.Clients;

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

    /// <summary>Получает содержимое исходного вложения для загрузки в другой мессенджер.</summary>
    /// <param name="attachment">Описание исходного файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Временный файл, который должен освободить вызывающий код.</returns>
    Task<MediaFile> DownloadAsync(MessageAttachment attachment, CancellationToken ct);

    /// <summary>Подготавливает вложение на целевой платформе до фиксации токена в очереди.</summary>
    /// <param name="attachment">Описание файла и ранее сохранённый токен.</param>
    /// <param name="source">Клиент исходного мессенджера.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Описание с результатом подготовки для повторного использования.</returns>
    Task<MessageAttachment> PrepareAttachmentAsync(MessageAttachment attachment, IMessengerClient source, CancellationToken ct);

    /// <summary>Отправляет одно вложение с подписью от имени бота.</summary>
    /// <param name="chatId">Идентификатор целевого чата.</param>
    /// <param name="text">Подпись с источником и автором.</param>
    /// <param name="attachment">Подготовленное описание вложения.</param>
    /// <param name="source">Клиент для скачивания исходного файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор отправленного сообщения.</returns>
    Task<string> SendAttachmentAsync(long chatId, string text, MessageAttachment attachment, IMessengerClient source, CancellationToken ct);
}