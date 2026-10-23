using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;
using MessengerBridge.Services;

namespace MessengerBridge.Clients;

/// <summary>
/// Клиент API MAX для проверки бота, long polling, сообщений и медиафайлов.
/// </summary>
/// <remarks>
/// Авторизация добавляется только к запросам API; загрузка и скачивание через CDN не передают токен бота.
/// </remarks>
/// <param name="http">HTTP-клиент, освобождаемый вместе с экземпляром MAX-клиента.</param>
/// <param name="options">Настройки бота, группы и long polling.</param>
public sealed class MaxClient(HttpClient http, BridgeOptions options) : IMessengerClient, IDisposable
{
    /// <summary>
    /// HTTP-клиент MAX без общего заголовка авторизации для внешних ссылок.
    /// </summary>
    private readonly HttpClient _http = http;
    /// <summary>
    /// Настройки исходной группы MAX и ожидания событий.
    /// </summary>
    private readonly BridgeOptions _options = options;
    /// <summary>
    /// Платформа MAX, обслуживаемая клиентом.
    /// </summary>
    public Platform Platform => Platform.Max;

    /// <summary>
    /// Выполняет авторизованный GET-запрос к API MAX.
    /// </summary>
    /// <param name="path">Относительный путь с параметрами запроса.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Корневой JSON-элемент ответа API.</returns>
    private Task<JsonElement> GetAsync(string path, CancellationToken ct) => ApiAsync(HttpMethod.Get, path, null, ct);

    /// <summary>Вызывает API MAX с авторизацией только для запросов к его базовому адресу.</summary>
    /// <param name="method">Метод HTTP.</param>
    /// <param name="path">Относительный путь API.</param>
    /// <param name="body">Тело JSON либо null.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Корневой элемент JSON-ответа.</returns>
    private async Task<JsonElement> ApiAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("Authorization", _options.Max.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await ApiTransport.ReadAsync(await _http.SendAsync(request, ct), ct);
    }

    /// <summary>
    /// Проверяет доступ к группе MAX, права бота и отсутствие активного Webhook.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Задача, представляющая выполнение операции.</returns>
    /// <exception cref="ConfigurationException">Тип группы, права бота или настройки Webhook не позволяют запустить мост.</exception>
    /// <exception cref="ApiException">API отклонил один из проверочных запросов.</exception>
    public async Task VerifyAsync(CancellationToken ct)
    {
        await GetAsync("me", ct);
        JsonElement subscriptions = await GetAsync("subscriptions", ct);
        if (subscriptions.GetProperty("subscriptions").GetArrayLength() != 0)
        {
            throw new ConfigurationException("В MAX настроены активные подписки Webhook. Удалите их перед запуском опроса событий.");
        }

        JsonElement chat = await GetAsync($"chats/{_options.Max.ChatId}", ct);
        if (chat.Text("type") != "chat")
        {
            throw new ConfigurationException("ChatId MAX должен указывать на групповой чат.");
        }

        JsonElement member = await GetAsync($"chats/{_options.Max.ChatId}/members/me", ct);
        if (!member.TryGetProperty("is_admin", out JsonElement admin) || admin.ValueKind != JsonValueKind.True)
        {
            throw new ConfigurationException("Назначьте бота MAX администратором группы.");
        }

        if (!member.TryGetProperty("permissions", out JsonElement permissions) ||
            permissions.ValueKind != JsonValueKind.Array ||
            !permissions.EnumerateArray().Any(p => p.GetString() == "read_all_messages"))
        {
            throw new ConfigurationException("Включите право администратора read_all_messages для бота MAX.");
        }
    }

    /// <summary>
    /// Получает события MAX и отбирает сообщения пользователей настроенной группы, включая вложения.
    /// </summary>
    /// <param name="cursor">Следующий offset Telegram либо marker MAX; null означает начальный запрос.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Отобранные сообщения и курсор продолжения чтения.</returns>
    public async Task<PollBatch> PollAsync(long? cursor, CancellationToken ct)
    {
        string marker = cursor is { } value ? "&marker=" + value.ToString(CultureInfo.InvariantCulture) : "";
        JsonElement root = await GetAsync($"updates?limit=100&timeout={_options.PollTimeoutSeconds}&types=message_created{marker}", ct);
        return ParseUpdates(root, _options.Max.ChatId);
    }

    /// <summary>
    /// Разбирает события MAX, отбирая текст, подписи и вложения пользователей исходной группы.
    /// </summary>
    /// <param name="root">JSON-ответ с массивом updates и необязательным marker.</param>
    /// <param name="chatId">Идентификатор исходной MAX-группы.</param>
    /// <returns>Отобранные сообщения и marker продолжения, если API его вернул.</returns>
    public static PollBatch ParseUpdates(JsonElement root, long chatId)
    {
        List<IncomingMessage> messages = [];
        foreach (JsonElement update in root.GetProperty("updates").EnumerateArray())
        {
            if (update.Text("update_type") != "message_created" ||
                !update.TryGetProperty("message", out JsonElement message) ||
                !message.GetProperty("recipient").TryGetProperty("chat_id", out JsonElement recipientChat) ||
                recipientChat.ValueKind != JsonValueKind.Number || !recipientChat.TryGetInt64(out long recipientId) ||
                recipientId != chatId ||
                !message.TryGetProperty("sender", out JsonElement sender) || sender.IsBot())
            {
                continue;
            }

            JsonElement body = message.GetProperty("body");
            string text = MessageContent.MaxText(body);
            IReadOnlyList<MessageAttachment> attachments = MessageContent.MaxAttachments(body);
            string? mid = body.Text("mid");
            if ((string.IsNullOrWhiteSpace(text) && attachments.Count == 0) || string.IsNullOrWhiteSpace(mid))
            {
                continue;
            }

            messages.Add(new IncomingMessage(mid, chatId, sender.Author(), text, attachments));
        }
        long? next = root.TryGetProperty("marker", out JsonElement marker) && marker.ValueKind == JsonValueKind.Number
            ? marker.GetInt64() : null;
        return new PollBatch(next, messages);
    }

    /// <summary>
    /// Отправляет обычный текст от имени бота MAX.
    /// </summary>
    /// <param name="chatId">Идентификатор целевого группового чата.</param>
    /// <param name="text">Текст сообщения с заголовком моста, без включения режима разметки.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Идентификатор отправленного сообщения.</returns>
    public async Task<string> SendAsync(long chatId, string text, CancellationToken ct)
    {
        JsonElement root = await ApiAsync(HttpMethod.Post,
            $"messages?chat_id={chatId}", new { text, notify = true }, ct);
        return root.GetProperty("message").GetProperty("body").GetProperty("mid").GetString()
            ?? throw new JsonException("В ответе API отсутствует идентификатор сообщения.");
    }

    /// <summary>
    /// Освобождает HTTP-клиент и принадлежащий ему обработчик запросов.
    /// </summary>
    public void Dispose() => _http.Dispose();

    /// <summary>Обновляет ссылку на исходное вложение MAX и скачивает файл без передачи токена бота CDN.</summary>
    /// <param name="attachment">Описание файла и исходного сообщения.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Временный файл для отправки в Telegram.</returns>
    public async Task<MediaFile> DownloadAsync(MessageAttachment attachment, CancellationToken ct)
    {
        string? url = attachment.Url;
        if (attachment.MessageId is { } messageId)
        {
            JsonElement result = await GetAsync("messages?message_ids=" + Uri.EscapeDataString(messageId), ct);
            if (result.TryGetProperty("messages", out JsonElement messages) && messages.GetArrayLength() > 0)
            {
                JsonElement body = messages[0].GetProperty("body");
                MessageAttachment? refreshed = MessageContent.MaxAttachments(body).FirstOrDefault(a =>
                    attachment.Token is { } token ? a.Token == token : a.Type == attachment.Type && a.FileName == attachment.FileName);
                url = refreshed?.Url ?? url;
            }
        }
        if (attachment.Type == "video" && attachment.Token is { } videoToken)
        {
            JsonElement video = await GetAsync("videos/" + Uri.EscapeDataString(videoToken), ct);
            if (video.TryGetProperty("urls", out JsonElement urls) && urls.ValueKind == JsonValueKind.Object)
            {
                url = new[] { "mp4_1080", "mp4_720", "mp4_480", "mp4_360", "mp4_240", "mp4_144" }
                    .Select(field => urls.Text(field)).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? url;
            }
        }
        string? name = attachment.FileName;
        if (name is null && Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
        {
            string candidate = Path.GetFileName(address.LocalPath);
            if (Path.HasExtension(candidate))
            {
                name = candidate;
            }
        }
        name ??= attachment.Type switch
        {
            "image" => "image.jpg",
            "video" => "video.mp4",
            "audio" => "audio.mp3",
            _ => "attachment.bin"
        };
        return await MediaTransfer.DownloadAsync(_http, MediaTransfer.Address(url).AbsoluteUri, attachment with { FileName = name }, ct);
    }

    /// <summary>Загружает вложение в MAX и возвращает токен, который нужно сохранить до отправки сообщения.</summary>
    /// <param name="attachment">Исходное описание или ранее подготовленное вложение.</param>
    /// <param name="source">Клиент для скачивания исходного файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Описание с токеном загруженного файла.</returns>
    public async Task<MessageAttachment> PrepareAttachmentAsync(MessageAttachment attachment, IMessengerClient source, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(attachment.UploadedToken))
        {
            return attachment;
        }
        using MediaFile file = await source.DownloadAsync(attachment, ct);
        string type = attachment.Type switch
        {
            "voice" => "audio",
            "animation" => file.ContentType == "image/gif" ? "image" : "video",
            "image" or "video" or "audio" => attachment.Type,
            _ => "file"
        };
        JsonElement endpoint = await ApiAsync(HttpMethod.Post, "uploads?type=" + type, null, ct);
        Uri url = MediaTransfer.Address(endpoint.Text("url"));
        using MultipartFormDataContent form = new();
        StreamContent content = new(file.Content);
        content.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out MediaTypeHeaderValue? mime)
            ? mime : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "data", file.FileName);
        using HttpResponseMessage response = await _http.PostAsync(url, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            await ApiTransport.ReadAsync(response, ct);
        }
        string uploaded = await response.Content.ReadAsStringAsync(ct);
        string? token = endpoint.Text("token");
        if (token is null)
        {
            using JsonDocument document = JsonDocument.Parse(uploaded);
            token = document.RootElement.Text("token");
            if (token is null && document.RootElement.TryGetProperty("photos", out JsonElement photos))
            {
                token = photos.EnumerateObject().Select(p => p.Value.Text("token")).FirstOrDefault(t => t is not null);
            }
        }
        return attachment with
        {
            Type = type,
            UploadedToken = token ?? throw new AttachmentException("MAX не вернул токен загруженного вложения.")
        };
    }

    /// <summary>Отправляет подготовленное вложение MAX, повторно используя сохранённый токен.</summary>
    /// <param name="chatId">Идентификатор целевого чата.</param>
    /// <param name="text">Подпись с автором и источником.</param>
    /// <param name="attachment">Вложение с токеном загрузки.</param>
    /// <param name="source">Клиент исходной платформы.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Идентификатор отправленного сообщения.</returns>
    public async Task<string> SendAttachmentAsync(long chatId, string text, MessageAttachment attachment,
        IMessengerClient source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attachment.UploadedToken))
        {
            throw new AttachmentException("В очереди отсутствует токен загруженного вложения MAX.");
        }
        JsonElement root = await ApiAsync(HttpMethod.Post, $"messages?chat_id={chatId}", new
        {
            text,
            notify = true,
            attachments = new[] { new { type = attachment.Type, payload = new { token = attachment.UploadedToken } } }
        }, ct);
        return root.GetProperty("message").GetProperty("body").GetProperty("mid").GetString()
            ?? throw new JsonException("В ответе API отсутствует идентификатор сообщения.");
    }
}