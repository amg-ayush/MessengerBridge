namespace MessengerBridge;

public interface IMessengerClient
{
    Platform Platform { get; }
    Task VerifyAsync(CancellationToken cancellationToken);
    Task<PollBatch> PollAsync(long? cursor, CancellationToken cancellationToken);
    Task<string> SendAsync(long chatId, string text, CancellationToken cancellationToken);
}