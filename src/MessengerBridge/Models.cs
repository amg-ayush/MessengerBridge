namespace MessengerBridge;

public enum Platform { Telegram, Max }

public sealed record IncomingMessage(string SourceId, long SourceChatId, string Author, string Text);
public sealed record PollBatch(long? NextCursor, IReadOnlyList<IncomingMessage> Messages);
public sealed record OutboxItem(long Id, Platform Source, long DestinationChatId, string Text,
    int Attempts, long NextAttemptAt);

public interface IMessengerClient
{
    Platform Platform { get; }
    Task VerifyAsync(CancellationToken cancellationToken);
    Task<PollBatch> PollAsync(long? cursor, CancellationToken cancellationToken);
    Task<string> SendAsync(long chatId, string text, CancellationToken cancellationToken);
}
