using System.Globalization;
using System.Text.Json;
using MessengerBridge.Clients;

namespace MessengerBridge.Services;

/// <summary>Формирует явно обозначенную текстовую копию Telegram-опроса без имитации голосования.</summary>
public static class PollTextFormatter
{
    /// <summary>Сохраняет вопрос, варианты, режим и снимок доступных результатов опроса.</summary>
    /// <param name="message">Сообщение Telegram с полем poll и реквизитами исходного чата.</param>
    /// <returns>Текстовая копия со ссылкой на оригинал, если для этого чата доступна ссылка на сообщение.</returns>
    /// <remarks>Копия не принимает голоса и не обновляется после пересылки. Правильные ответы и объяснение викторины не раскрываются.</remarks>
    public static string FormatTelegram(JsonElement message)
    {
        if (!message.TryGetProperty("poll", out JsonElement poll) || poll.ValueKind != JsonValueKind.Object)
        {
            return "[Содержимое опроса Telegram недоступно]";
        }
        bool quiz = poll.Text("type") == "quiz";
        List<string> lines =
        [
            (quiz ? "Викторина: " : "Опрос: ") + (poll.Text("question") ?? "[вопрос недоступен]"),
            "Текстовая копия. Голосование в этой копии недоступно; откройте исходный чат Telegram."
        ];
        if (poll.Text("description") is { Length: > 0 } description)
        {
            lines.Add(description);
        }
        List<string> modes = [];
        if (Boolean(poll, "is_anonymous") is { } anonymous)
        {
            modes.Add(anonymous ? "анонимный" : "неанонимный");
        }
        if (Boolean(poll, "allows_multiple_answers") is { } multiple)
        {
            modes.Add(multiple ? "можно выбрать несколько вариантов" : "один вариант ответа");
        }
        if (modes.Count > 0)
        {
            lines.Add("Режим: " + string.Join("; ", modes) + ".");
        }
        if (Boolean(poll, "is_closed") is { } closed)
        {
            lines.Add("Статус на момент пересылки: " + (closed ? "завершён." : "открыт."));
        }
        lines.Add("Варианты и данные API на момент пересылки:");
        int index = 0;
        if (poll.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement option in options.EnumerateArray())
            {
                string line = (++index).ToString(CultureInfo.InvariantCulture) + ". " + (option.Text("text") ?? "[вариант недоступен]");
                if (Number(option, "voter_count") is { } votes)
                {
                    line += " — голосов: " + votes.ToString(CultureInfo.InvariantCulture);
                }
                lines.Add(line);
            }
        }
        if (index == 0)
        {
            lines.Add("[Варианты ответа недоступны]");
        }
        if (Number(poll, "total_voter_count") is { } total)
        {
            lines.Add("Проголосовало участников: " + total.ToString(CultureInfo.InvariantCulture) + ".");
        }
        lines.Add("Голоса, результаты и закрытие опроса после пересылки не синхронизируются.");
        string? link = OriginalLink(message);
        if (link is not null)
        {
            lines.Add("Оригинал: " + link);
        }
        return string.Join('\n', lines);
    }

    /// <summary>Создаёт ссылку на сообщение публичного чата или приватной супергруппы.</summary>
    /// <param name="message">Сообщение Telegram с идентификатором и данными чата.</param>
    /// <returns>HTTPS-ссылка; null для обычной приватной группы или отсутствующих реквизитов.</returns>
    /// <remarks>Приватная ссылка доступна только пользователям с доступом к исходной супергруппе.</remarks>
    private static string? OriginalLink(JsonElement message)
    {
        if (Number(message, "message_id") is not { } messageId || messageId <= 0 ||
            !message.TryGetProperty("chat", out JsonElement chat) || chat.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (chat.Text("username") is { Length: > 0 } username)
        {
            return $"https://t.me/{Uri.EscapeDataString(username)}/{messageId.ToString(CultureInfo.InvariantCulture)}";
        }
        string? chatId = Number(chat, "id")?.ToString(CultureInfo.InvariantCulture);
        if (chat.Text("type") is "supergroup" or "channel" && chatId is { Length: > 4 } &&
            chatId.StartsWith("-100", StringComparison.Ordinal))
        {
            return $"https://t.me/c/{chatId[4..]}/{messageId.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    /// <summary>Читает необязательный флаг без подстановки вымышленных значений.</summary>
    /// <param name="item">JSON-объект.</param>
    /// <param name="field">Имя флага.</param>
    /// <returns>Значение флага либо null.</returns>
    private static bool? Boolean(JsonElement item, string field) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Читает необязательный идентификатор или число голосов.</summary>
    /// <param name="item">JSON-объект.</param>
    /// <param name="field">Имя поля.</param>
    /// <returns>Целое число либо null.</returns>
    private static long? Number(JsonElement item, string field) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
}