namespace MessengerBridge.Configuration;

public sealed class BridgeOptions
{
    public EndpointOptions Telegram { get; set; } = new();
    public EndpointOptions Max { get; set; } = new();
    public string DatabasePath { get; set; } = "data/bridge.db";
    public int PollTimeoutSeconds { get; set; } = 30;
    public long? InitialMaxMarker { get; set; }
    // Conservative group flood limit; each destination has its own sender.
    public int SendIntervalMilliseconds { get; set; } = 3100;

    public void Validate(bool requireTokens)
    {
        if (PollTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Bridge:PollTimeoutSeconds must be 1..60.");
        if (SendIntervalMilliseconds < 3100)
            throw new InvalidOperationException("Bridge:SendIntervalMilliseconds must be >= 3100.");
        if (string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidOperationException("Bridge:DatabasePath is required.");
        if (InitialMaxMarker < 0)
            throw new InvalidOperationException("Bridge:InitialMaxMarker must be >= 0 or null.");
        ValidateEndpoint(Telegram, "Telegram", requireTokens);
        ValidateEndpoint(Max, "Max", requireTokens);
    }

    private static void ValidateEndpoint(EndpointOptions options, string name, bool requireToken)
    {
        if (options.ChatId == 0)
            throw new InvalidOperationException($"Bridge:{name}:ChatId is required (Int64).");
        if (requireToken && (string.IsNullOrWhiteSpace(options.Token) || options.Token.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException($"Set Bridge:{name}:Token via User Secrets or environment.");
        if (name == "Telegram" && requireToken &&
            !System.Text.RegularExpressions.Regex.IsMatch(options.Token, @"^\d+:[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException("Bridge:Telegram:Token has an invalid format.");
        if (!string.IsNullOrWhiteSpace(options.ProxyUrl) &&
            (!Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out Uri? uri) ||
             uri.Scheme is not ("http" or "https" or "socks5")))
            throw new InvalidOperationException($"Bridge:{name}:ProxyUrl must be http, https or socks5.");
    }
}