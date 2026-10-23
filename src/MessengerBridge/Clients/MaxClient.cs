using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

/// <summary>
/// Клиент API MAX для проверки бота, long polling и отправки текстовых сообщений.
/// </summary>
public sealed class MaxClient : IMessengerClient, IDisposable
{
    /// <summary>
    /// HTTP-клиент MAX с заголовком авторизации бота.
    /// </summary>
    private readonly HttpClient _http;
    /// <summary>
    /// Настройки исходной группы MAX и ожидания событий.
    /// </summary>
    private readonly BridgeOptions _options;
    /// <summary>
    /// Платформа MAX, обслуживаемая клиентом.
    /// </summary>
    public Platform Platform => Platform.Max;

    /// <summary>
    /// Создаёт клиент MAX и добавляет токен бота в заголовок Authorization.
    /// </summary>
    /// <param name="http">HTTP-клиент, освобождаемый вместе с экземпляром MAX-клиента.</param>
    /// <param name="options">Настройки бота, группы и long polling.</param>
    public MaxClient(HttpClient http, BridgeOptions options)
    {
        _http = http;
        _options = options;
        _http.DefaultRequestHeaders.Add("Authorization", options.Max.Token);
    }

    /// <summary>
    /// Выполняет авторизованный GET-запрос к API MAX.
    /// </summary>
    /// <param name="path">Относительный путь с параметрами запроса.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Корневой JSON-элемент ответа API.</returns>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
        await ApiTransport.ReadAsync(await _http.GetAsync(path, ct), ct);

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
    /// Получает события MAX и отбирает текстовые сообщения пользователей настроенной группы.
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
    /// Разбирает события MAX, отбирая новые непустые текстовые сообщения пользователей исходной группы.
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
            string? text = body.Text("text");
            string? mid = body.Text("mid");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(mid))
            {
                continue;
            }

            messages.Add(new IncomingMessage(mid, chatId, sender.Author(), text));
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
        JsonElement root = await ApiTransport.ReadAsync(await _http.PostAsJsonAsync(
            $"messages?chat_id={chatId}", new { text, notify = true }, ct), ct);
        return root.GetProperty("message").GetProperty("body").GetProperty("mid").GetString()
            ?? throw new JsonException("В ответе API отсутствует идентификатор сообщения.");
    }

    /// <summary>
    /// Освобождает HTTP-клиент и принадлежащий ему обработчик запросов.
    /// </summary>
    public void Dispose() => _http.Dispose();
}