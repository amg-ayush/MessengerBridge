using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

public sealed class TelegramClient(HttpClient http, BridgeOptions options) : IMessengerClient, IDisposable
{
    public Platform Platform => Platform.Telegram;
    private string Method(string method) => $"/bot{options.Telegram.Token}/{method}";

    private async Task<JsonElement> CallAsync(string method, object body, CancellationToken ct)
    {
        JsonElement root = await ApiTransport.ReadAsync(await http.PostAsJsonAsync(Method(method), body, ct), ct);
        if (!root.GetProperty("ok").GetBoolean())
            throw new ApiException(root.TryGetProperty("error_code", out JsonElement code) ? code.GetInt32() : 500);
        return root.GetProperty("result");
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        JsonElement me = await CallAsync("getMe", new { }, ct);
        JsonElement webhook = await CallAsync("getWebhookInfo", new { }, ct);
        if (!string.IsNullOrEmpty(webhook.Text("url")))
            throw new ConfigurationException("Telegram has an active Webhook. Remove it explicitly before polling.");
        JsonElement chat = await CallAsync("getChat", new { chat_id = options.Telegram.ChatId }, ct);
        if (chat.Text("type") is not ("group" or "supergroup"))
            throw new ConfigurationException("Telegram ChatId must point to a group/supergroup.");
        JsonElement member = await CallAsync("getChatMember", new
        {
            chat_id = options.Telegram.ChatId, user_id = me.GetProperty("id").GetInt64()
        }, ct);
        if (member.Text("status") is not ("administrator" or "creator"))
            throw new ConfigurationException("Make the Telegram bot a group administrator.");
    }

    public async Task<PollBatch> PollAsync(long? cursor, CancellationToken ct)
    {
        JsonElement updates = await CallAsync("getUpdates", new
        {
            offset = cursor, limit = 100, timeout = options.PollTimeoutSeconds,
            allowed_updates = new[] { "message" }
        }, ct);
        return ParseUpdates(updates, options.Telegram.ChatId, cursor);
    }

    public static PollBatch ParseUpdates(JsonElement updates, long chatId, long? cursor)
    {
        var messages = new List<IncomingMessage>();
        var next = cursor;
        foreach (JsonElement update in updates.EnumerateArray())
        {
            var candidate = checked(update.GetProperty("update_id").GetInt64() + 1);
            next = next is { } value ? Math.Max(value, candidate) : candidate;
            if (!update.TryGetProperty("message", out JsonElement message) ||
                message.GetProperty("chat").GetProperty("id").GetInt64() != chatId) continue;
            if (message.TryGetProperty("migrate_to_chat_id", out JsonElement migrated))
                throw new ConfigurationException($"Telegram group migrated: set ChatId={migrated.GetInt64()} (see README).");
            // Ignore all bots, anonymous admins/channel senders and service messages.
            if (message.TryGetProperty("sender_chat", out _) ||
                !message.TryGetProperty("from", out JsonElement sender) || sender.IsBot()) continue;
            var text = message.Text("text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(new IncomingMessage(
                message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture),
                chatId, sender.Author(), text));
        }
        return new PollBatch(next, messages);
    }

    public async Task<string> SendAsync(long chatId, string text, CancellationToken ct)
    {
        JsonElement message = await CallAsync("sendMessage", new
        {
            chat_id = chatId, text, link_preview_options = new { is_disabled = true }
        }, ct);
        return message.GetProperty("message_id").GetInt64().ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose() => http.Dispose();
}
