using System.Net;
using System.Text.Json;
using MessengerBridge.Configuration;

namespace MessengerBridge.Clients;

/// <summary>
/// Создаёт HTTP-клиенты и читает ответы API с безопасной обработкой ошибок.
/// </summary>
public static class ApiTransport
{
    /// <summary>
    /// Создаёт HTTP-клиент с явной настройкой прокси, проверкой TLS и запретом перенаправлений.
    /// </summary>
    /// <param name="options">Параметры подключения; пустой адрес прокси отключает также системный прокси.</param>
    /// <param name="baseAddress">Базовый адрес API мессенджера.</param>
    /// <returns>HTTP-клиент с тайм-аутом 90 секунд, который должен освободить вызывающий код.</returns>
    public static HttpClient Create(EndpointOptions options, Uri baseAddress)
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            // Deliberate: ignore environment/system proxies when ProxyUrl is blank.
            UseProxy = !string.IsNullOrWhiteSpace(options.ProxyUrl)
        };
        if (handler.UseProxy)
        {
            handler.Proxy = new WebProxy(options.ProxyUrl);
        }

        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>
    /// Читает JSON-ответ и преобразует ошибку API в исключение без чувствительных данных.
    /// </summary>
    /// <param name="response">HTTP-ответ, который будет освобождён после чтения.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Копия корневого JSON-элемента, независимая от времени жизни документа.</returns>
    /// <exception cref="ApiException">HTTP-статус указывает на неуспешный ответ; сведения о повторе и миграции извлекаются при наличии.</exception>
    /// <exception cref="JsonException">Успешный ответ не содержит корректного JSON.</exception>
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            string json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                double? retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                if (retry is null && response.Headers.RetryAfter?.Date is { } date)
                {
                    retry = Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
                }

                int? seconds = retry is { } value ? (int)Math.Ceiling(Math.Min(value, int.MaxValue)) : null;
                long? migrate = null;
                bool attachmentNotReady = false;
                try
                {
                    using JsonDocument error = JsonDocument.Parse(json);
                    attachmentNotReady = error.RootElement.Text("code") == "attachment.not.ready";
                    if (error.RootElement.TryGetProperty("parameters", out JsonElement parameters))
                    {
                        if (parameters.TryGetProperty("retry_after", out JsonElement after))
                        {
                            seconds = after.GetInt32();
                        }

                        if (parameters.TryGetProperty("migrate_to_chat_id", out JsonElement id))
                        {
                            migrate = id.GetInt64();
                        }
                    }
                }
                catch (JsonException) { }
                throw new ApiException(attachmentNotReady ? 503 : (int)response.StatusCode,
                    attachmentNotReady ? seconds ?? 3 : seconds, migrate);
            }
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}