namespace MessengerBridge.Services;

public static class MessageFormatter
{
    // Leave room below both API limits and never split UTF-16 surrogate pairs.
    public const int MaxLength = 3500;

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

        string header = $"[{(source == Platform.Max ? "MAX" : "Telegram")}] {author}\n";
        List<string> parts = [];
        string remaining = message.Text;
        while (remaining.Length > 0)
        {
            int length = SafeLength(remaining, MaxLength - header.Length);
            parts.Add(header + remaining[..length]);
            remaining = remaining[length..];
        }
        return parts;
    }

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