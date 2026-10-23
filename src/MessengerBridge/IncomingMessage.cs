namespace MessengerBridge;

public sealed record IncomingMessage(string SourceId, long SourceChatId, string Author, string Text);