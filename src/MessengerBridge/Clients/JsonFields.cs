using System.Text.Json;

namespace MessengerBridge.Clients;

/// <summary>
/// Вспомогательные операции чтения текстовых полей и данных автора из JSON API.
/// </summary>
internal static class JsonFields
{
    /// <summary>
    /// Читает строковое поле JSON-объекта без преобразования других типов.
    /// </summary>
    /// <param name="element">Проверяемый JSON-элемент.</param>
    /// <param name="name">Имя строкового поля.</param>
    /// <returns>Строка поля либо null, если объект или строковое поле отсутствуют.</returns>
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Проверяет признак бота в JSON-объекте отправителя.
    /// </summary>
    /// <param name="element">JSON-объект отправителя.</param>
    /// <returns>true, только если поле is_bot содержит логическое значение true.</returns>
    public static bool IsBot(this JsonElement element) =>
        element.TryGetProperty("is_bot", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Извлекает имя автора из поля name либо объединяет first_name и last_name.
    /// </summary>
    /// <param name="element">JSON-объект отправителя.</param>
    /// <returns>Имя автора; пустая строка, если подходящие поля отсутствуют.</returns>
    public static string Author(this JsonElement element) => element.Text("name") ??
        string.Join(" ", new[] { element.Text("first_name"), element.Text("last_name") }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
}