using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;
using MessengerBridge.Services;

namespace MessengerBridge.Clients;

/// <summary>
/// Клиент API Telegram для проверки бота, long polling, сообщений и медиафайлов.
/// </summary>
/// <param name="http">HTTP-клиент с настроенным базовым адресом и прокси; освобождается вместе с клиентом мессенджера.</param>
/// <param name="options">Настройки подключения, исходной группы и long polling.</param>
public sealed class TelegramClient(HttpClient http, BridgeOptions options) : IMessengerClient, IDisposable
{
    /// <summary>
    /// Платформа Telegram, обслуживаемая клиентом.
    /// </summary>
    public Platform Platform => Platform.Telegram;
    /// <summary>
    /// Формирует относительный путь метода Telegram с токеном бота.
    /// </summary>
    /// <param name="method">Имя метода Bot API.</param>
    /// <returns>Путь запроса; содержит секрет и не должен попадать в журнал.</returns>
    private string Method(string method) => $"/bot{options.Telegram.Token}/{method}";

    /// <summary>
    /// Вызывает метод Telegram через POST и извлекает поле result.
    /// </summary>
    /// <param name="method">Имя метода Bot API.</param>
    /// <param name="body">Объект для сериализации в JSON-тело запроса.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат вызова Telegram API.</returns>
    /// <exception cref="ApiException">HTTP-ответ или поле ok указывают на ошибку API.</exception>
    private async Task<JsonElement> CallAsync(string method, object body, CancellationToken ct)
    {
        JsonElement root = await ApiTransport.ReadAsync(await http.PostAsJsonAsync(Method(method), body, ct), ct);
        if (!root.GetProperty("ok").GetBoolean())
        {
            throw new ApiException(root.TryGetProperty("error_code", out JsonElement code) ? code.GetInt32() : 500);
        }

        return root.GetProperty("result");
    }

    /// <summary>
    /// Проверяет доступ к группе Telegram, права бота и отсутствие активного Webhook.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Задача, представляющая выполнение операции.</returns>
    /// <exception cref="ConfigurationException">Тип группы, права бота или настройки Webhook не позволяют запустить мост.</exception>
    /// <exception cref="ApiException">API отклонил один из проверочных запросов.</exception>
    public async Task VerifyAsync(CancellationToken ct)
    {
        JsonElement me = await CallAsync("getMe", new { }, ct);
        JsonElement webhook = await CallAsync("getWebhookInfo", new { }, ct);
        if (!string.IsNullOrEmpty(webhook.Text("url")))
        {
            throw new ConfigurationException("В Telegram настроен активный Webhook. Удалите его перед запуском опроса событий.");
        }

        JsonElement chat = await CallAsync("getChat", new { chat_id = options.Telegram.ChatId }, ct);
        if (chat.Text("type") is not ("group" or "supergroup"))
        {
            throw new ConfigurationException("ChatId Telegram должен указывать на группу или супергруппу.");
        }

        JsonElement member = await CallAsync("getChatMember", new
        {
            chat_id = options.Telegram.ChatId,
            user_id = me.GetProperty("id").GetInt64()
        }, ct);
        if (member.Text("status") is not ("administrator" or "creator"))
        {
            throw new ConfigurationException("Назначьте бота Telegram администратором группы.");
        }
    }

    /// <summary>
    /// Получает события Telegram и отбирает сообщения пользователей настроенной группы, включая вложения.
    /// </summary>
    /// <param name="cursor">Следующий offset Telegram либо marker MAX; null означает начальный запрос.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Отобранные сообщения и курсор продолжения чтения.</returns>
    public async Task<PollBatch> PollAsync(long? cursor, CancellationToken ct)
    {
        JsonElement updates = await CallAsync("getUpdates", new
        {
            offset = cursor,
            limit = 100,
            timeout = options.PollTimeoutSeconds,
            allowed_updates = new[] { "message" }
        }, ct);
        return ParseUpdates(updates, options.Telegram.ChatId, cursor);
    }

    /// <summary>
    /// Разбирает текст, подписи и вложения Telegram, пропуская ботов, другие чаты и анонимных отправителей.
    /// </summary>
    /// <param name="updates">Массив событий из поля result ответа getUpdates.</param>
    /// <param name="chatId">Идентификатор исходной Telegram-группы.</param>
    /// <param name="cursor">Текущий offset, который не должен уменьшаться.</param>
    /// <returns>Сообщения группы и offset после максимального update_id, включая отфильтрованные события.</returns>
    /// <exception cref="ConfigurationException">Группа мигрировала в supergroup и требуется обновить её идентификатор.</exception>
    public static PollBatch ParseUpdates(JsonElement updates, long chatId, long? cursor)
    {
        List<IncomingMessage> messages = [];
        long? next = cursor;
        foreach (JsonElement update in updates.EnumerateArray())
        {
            long candidate = checked(update.GetProperty("update_id").GetInt64() + 1);
            next = next is { } value ? Math.Max(value, candidate) : candidate;
            if (!update.TryGetProperty("message", out JsonElement message) ||
                message.GetProperty("chat").GetProperty("id").GetInt64() != chatId)
            {
                continue;
            }

            if (message.TryGetProperty("migrate_to_chat_id", out JsonElement migrated))
            {
                throw new ConfigurationException($"Группа Telegram преобразована в супергруппу: задайте ChatId={migrated.GetInt64()} (см. README).");
            }
            // Ignore all bots, anonymous admins/channel senders and service messages.
            if (message.TryGetProperty("sender_chat", out _) ||
                !message.TryGetProperty("from", out JsonElement sender) || sender.IsBot())
            {
                continue;
            }

            string text = MessageContent.TelegramText(message);
            MessageAttachment[] attachments = [.. MessageContent.TelegramAttachments(message).Where(a => !string.IsNullOrWhiteSpace(a.FileId))];
            if (string.IsNullOrWhiteSpace(text) && attachments.Length == 0)
            {
                continue;
            }

            messages.Add(new IncomingMessage(
                message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture),
                chatId, sender.Author(), text, attachments));
        }
        return new PollBatch(next, messages);
    }

    /// <summary>
    /// Отправляет обычный текст от имени бота Telegram.
    /// </summary>
    /// <param name="chatId">Идентификатор целевого группового чата.</param>
    /// <param name="text">Текст сообщения с заголовком моста, без включения режима разметки.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Идентификатор отправленного сообщения.</returns>
    public async Task<string> SendAsync(long chatId, string text, CancellationToken ct)
    {
        JsonElement message = await CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text
        }, ct);
        return message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Освобождает HTTP-клиент и принадлежащий ему обработчик запросов.
    /// </summary>
    public void Dispose() => http.Dispose();

    /// <summary>Получает свежую ссылку getFile и скачивает исходный файл через прокси Telegram.</summary>
    /// <param name="attachment">Описание исходного файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Временный файл для загрузки в MAX.</returns>
    /// <exception cref="AttachmentException">Размер превышает предел скачивания Telegram или файл недоступен.</exception>
    public async Task<MediaFile> DownloadAsync(MessageAttachment attachment, CancellationToken ct)
    {
        if (attachment.Size > 20L * 1024 * 1024)
        {
            throw new AttachmentException("Облачный Telegram Bot API позволяет скачивать файлы размером не более 20 МиБ.");
        }
        JsonElement file = await CallAsync("getFile", new { file_id = attachment.FileId }, ct);
        string path = file.Text("file_path") ?? throw new AttachmentException("Telegram не предоставил путь к файлу.");
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        string url = new Uri(http.BaseAddress!, $"file/bot{options.Telegram.Token}/{escaped}").AbsoluteUri;
        return await MediaTransfer.DownloadAsync(http, url, attachment, ct);
    }

    /// <summary>Оставляет исходное описание: Telegram загружает файл непосредственно при отправке.</summary>
    /// <param name="attachment">Описание файла.</param>
    /// <param name="source">Клиент исходной платформы.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Исходное описание файла.</returns>
    public Task<MessageAttachment> PrepareAttachmentAsync(MessageAttachment attachment, IMessengerClient source, CancellationToken ct) =>
        Task.FromResult(attachment);

    /// <summary>Скачивает файл MAX и отправляет multipart-запрос с подписью в Telegram.</summary>
    /// <param name="chatId">Идентификатор группы Telegram.</param>
    /// <param name="text">Подпись с автором и источником.</param>
    /// <param name="attachment">Описание исходного файла.</param>
    /// <param name="source">Клиент MAX для скачивания файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор отправленного сообщения.</returns>
    public async Task<string> SendAttachmentAsync(long chatId, string text, MessageAttachment attachment,
        IMessengerClient source, CancellationToken ct)
    {
        using MediaFile file = await source.DownloadAsync(attachment, ct);
        string field = attachment.Type switch
        {
            "image" when file.Content.Length <= 10L * 1024 * 1024 &&
                !new[] { ".heic", ".tif", ".tiff", ".bmp" }.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()) => "photo",
            "video" when !new[] { ".webm", ".mkv", ".mov" }.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()) => "video",
            "audio" when file.ContentType.StartsWith("audio/ogg", StringComparison.OrdinalIgnoreCase) ||
                new[] { ".ogg", ".opus" }.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()) => "voice",
            "audio" when Path.GetExtension(file.FileName).ToLowerInvariant() is ".mp3" or ".m4a" => "audio",
            "voice" => "voice",
            "animation" => "animation",
            _ => "document"
        };
        string method = field switch
        {
            "photo" => "sendPhoto",
            "video" => "sendVideo",
            "audio" => "sendAudio",
            "voice" => "sendVoice",
            "animation" => "sendAnimation",
            _ => "sendDocument"
        };
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
        form.Add(new StringContent(text), "caption");
        StreamContent content = new(file.Content);
        content.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out MediaTypeHeaderValue? mime)
            ? mime : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, field, file.FileName);
        JsonElement root = await ApiTransport.ReadAsync(await http.PostAsync(Method(method), form, ct), ct);
        if (!root.GetProperty("ok").GetBoolean())
        {
            throw new ApiException(root.TryGetProperty("error_code", out JsonElement code) ? code.GetInt32() : 500);
        }
        return root.GetProperty("result").GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture);
    }
}