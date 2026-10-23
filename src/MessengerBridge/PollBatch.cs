namespace MessengerBridge;

public sealed record PollBatch(long? NextCursor, IReadOnlyList<IncomingMessage> Messages);