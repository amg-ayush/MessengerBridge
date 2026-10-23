using System.Text.Json;

namespace MessengerBridge.Clients;

internal static class JsonFields
{
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool IsBot(this JsonElement element) =>
        element.TryGetProperty("is_bot", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public static string Author(this JsonElement element) => element.Text("name") ??
        string.Join(" ", new[] { element.Text("first_name"), element.Text("last_name") }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
}