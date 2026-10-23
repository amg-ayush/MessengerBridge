namespace MessengerBridge.Configuration;

public sealed class EndpointOptions
{
    public string Token { get; set; } = "";
    public long ChatId { get; set; }
    public string ProxyUrl { get; set; } = "";
}