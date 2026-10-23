namespace MessengerBridge.Tests;

/// <summary>
/// Подменяет HTTP-транспорт в тестах, передавая запросы заданному обработчику.
/// </summary>
/// <param name="send">Обработчик, проверяющий запрос и формирующий тестовый ответ.</param>
internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    /// <summary>
    /// Передаёт запрос тестовому обработчику без обращения к сети.
    /// </summary>
    /// <param name="request">HTTP-запрос клиента мессенджера.</param>
    /// <param name="ct">Токен отмены; подставной обработчик его не использует.</param>
    /// <returns>Задача с ответом тестового обработчика.</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
}