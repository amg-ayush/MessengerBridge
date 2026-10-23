using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

public sealed class MaxClient : IMessengerClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly BridgeOptions _options;
    public Platform Platform => Platform.Max;

    public MaxClient(HttpClient http, BridgeOptions options)
    {
        _http = http;
        _options = options;
        _http.DefaultRequestHeaders.Add("Authorization", options.Max.Token);
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
        await ApiTransport.ReadAsync(await _http.GetAsync(path, ct), ct);

    public async Task VerifyAsync(CancellationToken ct)
    {
        await GetAsync("me", ct);
        JsonElement subscriptions = await GetAsync("subscriptions", ct);
        if (subscriptions.GetProperty("subscriptions").GetArrayLength() != 0)
            throw new ConfigurationException("MAX has active Webhook subscriptions. Remove them explicitly before polling.");
        JsonElement chat = await GetAsync($"chats/{_options.Max.ChatId}", ct);
        if (chat.Text("type") != "chat")
            throw new ConfigurationException("MAX ChatId must point to a group chat.");
        JsonElement member = await GetAsync($"chats/{_options.Max.ChatId}/members/me", ct);
        if (!member.TryGetProperty("is_admin", out JsonElement admin) || admin.ValueKind != JsonValueKind.True)
            throw new ConfigurationException("Make the MAX bot a group administrator.");
        if (!member.TryGetProperty("permissions", out JsonElement permissions) ||
            permissions.ValueKind != JsonValueKind.Array ||
            !permissions.EnumerateArray().Any(p => p.GetString() == "read_all_messages"))
            throw new ConfigurationException("Enable the MAX bot administrator permission read_all_messages.");
    }

    public async Task<PollBatch> PollAsync(long? cursor, CancellationToken ct)
    {
        var marker = cursor is { } value ? "&marker=" + value.ToString(CultureInfo.InvariantCulture) : "";
        JsonElement root = await GetAsync($"updates?limit=100&timeout={_options.PollTimeoutSeconds}&types=message_created{marker}", ct);
        return ParseUpdates(root, _options.Max.ChatId);
    }

    public static PollBatch ParseUpdates(JsonElement root, long chatId)
    {
        var messages = new List<IncomingMessage>();
        foreach (JsonElement update in root.GetProperty("updates").EnumerateArray())
        {
            if (update.Text("update_type") != "message_created" ||
                !update.TryGetProperty("message", out JsonElement message) ||
                !message.GetProperty("recipient").TryGetProperty("chat_id", out JsonElement recipientChat) ||
                recipientChat.ValueKind != JsonValueKind.Number || !recipientChat.TryGetInt64(out var recipientId) ||
                recipientId != chatId ||
                !message.TryGetProperty("sender", out JsonElement sender) || sender.IsBot()) continue;
            JsonElement body = message.GetProperty("body");
            var text = body.Text("text");
            var mid = body.Text("mid");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(mid)) continue;
            messages.Add(new IncomingMessage(mid, chatId, sender.Author(), text));
        }
        long? next = root.TryGetProperty("marker", out JsonElement marker) && marker.ValueKind == JsonValueKind.Number
            ? marker.GetInt64() : null;
        return new PollBatch(next, messages);
    }

    public async Task<string> SendAsync(long chatId, string text, CancellationToken ct)
    {
        JsonElement root = await ApiTransport.ReadAsync(await _http.PostAsJsonAsync(
            $"messages?chat_id={chatId}", new { text, notify = true }, ct), ct);
        return root.GetProperty("message").GetProperty("body").GetProperty("mid").GetString()
            ?? throw new JsonException("Missing message ID.");
    }

    public void Dispose() => _http.Dispose();
}
