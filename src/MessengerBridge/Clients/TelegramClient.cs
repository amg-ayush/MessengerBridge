using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

/// <summary>
/// Клиент API Telegram для проверки бота, long polling и отправки текстовых сообщений.
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
            throw new ConfigurationException("Telegram has an active Webhook. Remove it explicitly before polling.");
        }

        JsonElement chat = await CallAsync("getChat", new { chat_id = options.Telegram.ChatId }, ct);
        if (chat.Text("type") is not ("group" or "supergroup"))
        {
            throw new ConfigurationException("Telegram ChatId must point to a group/supergroup.");
        }

        JsonElement member = await CallAsync("getChatMember", new
        {
            chat_id = options.Telegram.ChatId,
            user_id = me.GetProperty("id").GetInt64()
        }, ct);
        if (member.Text("status") is not ("administrator" or "creator"))
        {
            throw new ConfigurationException("Make the Telegram bot a group administrator.");
        }
    }

    /// <summary>
    /// Получает события Telegram и отбирает текстовые сообщения пользователей настроенной группы.
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
    /// Разбирает события Telegram, пропуская ботов, другие чаты, анонимных отправителей и нетекстовые сообщения.
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
                throw new ConfigurationException($"Telegram group migrated: set ChatId={migrated.GetInt64()} (see README).");
            }
            // Ignore all bots, anonymous admins/channel senders and service messages.
            if (message.TryGetProperty("sender_chat", out _) ||
                !message.TryGetProperty("from", out JsonElement sender) || sender.IsBot())
            {
                continue;
            }

            string? text = message.Text("text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            messages.Add(new IncomingMessage(
                message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture),
                chatId, sender.Author(), text));
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
            text,
            link_preview_options = new { is_disabled = true }
        }, ct);
        return message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Освобождает HTTP-клиент и принадлежащий ему обработчик запросов.
    /// </summary>
    public void Dispose() => http.Dispose();
}