namespace MessengerBridge;

public sealed record OutboxItem(long Id, Platform Source, long DestinationChatId, string Text,
    int Attempts, long NextAttemptAt);