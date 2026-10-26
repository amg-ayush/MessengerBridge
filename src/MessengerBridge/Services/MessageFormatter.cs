namespace MessengerBridge.Services;

/// <summary>
/// Добавляет источник и автора, разделяя длинные сообщения на части для отправки.
/// </summary>
public static class MessageFormatter
{
    // Leave room below both API limits and never split UTF-16 surrogate pairs.
    /// <summary>
    /// Максимальная длина части с заголовком в кодовых единицах UTF-16.
    /// </summary>
    public const int MaxLength = 3500;

    /// <summary>
    /// Формирует части сообщения с заголовком автора без разрыва суррогатных пар UTF-16.
    /// </summary>
    /// <param name="source">Платформа, из которой получено сообщение.</param>
    /// <param name="message">Исходное сообщение с именем автора и текстом.</param>
    /// <returns>Части до MaxLength для текста или до 1000 символов для медиа; для файла без подписи возвращается заголовок.</returns>
    /// <remarks>Имя автора нормализуется и ограничивается 100 кодовыми единицами UTF-16. Заголовок повторяется в каждой части; границы графем не гарантируются.</remarks>
    public static IReadOnlyList<string> Format(Platform source, IncomingMessage message)
    {
        string author = string.Join(" ", message.Author.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (author.Length > 100)
        {
            author = author[..SafeLength(author, 100)];
        }

        if (author.Length == 0)
        {
            author = "Участник";
        }

        string header = $"[{(source == Platform.Max ? "MAX" : "Telegram")}] {author}\n\n";
        bool hasMedia = message.Attachments is { Count: > 0 };
        int limit = hasMedia ? 1000 : MaxLength;
        List<string> parts = [];
        string remaining = message.Text;
        if (remaining.Length == 0 && hasMedia)
        {
            parts.Add(header.TrimEnd());
        }
        while (remaining.Length > 0)
        {
            int length = SafeLength(remaining, limit - header.Length);
            parts.Add(header + remaining[..length]);
            remaining = remaining[length..];
        }
        return parts;
    }

    /// <summary>
    /// Выбирает длину префикса, не разрывающую суррогатную пару UTF-16.
    /// </summary>
    /// <param name="text">Строка, из которой выделяется префикс.</param>
    /// <param name="limit">Максимально допустимое число кодовых единиц UTF-16.</param>
    /// <returns>Длина префикса в пределах лимита и длины строки.</returns>
    private static int SafeLength(string text, int limit)
    {
        int length = Math.Min(text.Length, limit);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]) &&
            char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return length;
    }
}