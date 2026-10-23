using MessengerBridge.Clients;
using MessengerBridge.Configuration;
using MessengerBridge.Persistence;

namespace MessengerBridge.Workers;

/// <summary>
/// Параллельно получает сообщения двух мессенджеров и отправляет сохранённые части в противоположные чаты.
/// </summary>
/// <param name="telegram">Клиент Telegram.</param>
/// <param name="max">Клиент MAX.</param>
/// <param name="store">Общее хранилище курсоров и очереди.</param>
/// <param name="options">Настройки чатов, начального marker и частоты отправки.</param>
/// <param name="logger">Журнал событий без содержимого переписки.</param>
/// <param name="lifetime">Управление остановкой приложения при неисправимой ошибке.</param>
public sealed class BridgeWorker(TelegramClient telegram, MaxClient max, BridgeStore store,
    BridgeOptions options, ILogger<BridgeWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    /// <summary>
    /// Запускает два цикла приёма и два независимых цикла отправки до остановки приложения.
    /// </summary>
    /// <param name="stoppingToken">Токен остановки фонового сервиса.</param>
    /// <returns>Задача, завершающаяся после остановки всех циклов.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(
                ReceiveAsync(telegram, options.Max.ChatId, stoppingToken),
                ReceiveAsync(max, options.Telegram.ChatId, stoppingToken),
                RelayAsync(Platform.Telegram, max, stoppingToken),
                RelayAsync(Platform.Max, telegram, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogCritical("Bridge stopped: {Reason}", SafeError.Describe(ex));
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Записывает безопасную причину неисправимой ошибки и запрашивает остановку с кодом 1.
    /// </summary>
    /// <param name="platform">Платформа, при обработке которой возникла ошибка.</param>
    /// <param name="ex">Причина остановки.</param>
    private void StopForConfiguration(Platform platform, Exception ex)
    {
        logger.LogCritical("{Platform}: {Reason}. Fix configuration and restart.",
            platform, SafeError.Describe(ex));
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    /// <summary>
    /// Проверяет подключение, получает события и сохраняет их вместе с курсором, повторяя временные ошибки.
    /// </summary>
    /// <param name="client">Клиент исходной платформы.</param>
    /// <param name="destinationChatId">Идентификатор целевого группового чата.</param>
    /// <param name="ct">Токен остановки цикла приёма.</param>
    /// <returns>Задача цикла приёма до отмены или неисправимой ошибки.</returns>
    private async Task ReceiveAsync(IMessengerClient client, long destinationChatId, CancellationToken ct)
    {
        bool verified = false;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!verified)
                {
                    await client.VerifyAsync(ct);
                    verified = true;
                    logger.LogInformation("{Platform}: bot, group and polling checks passed.", client.Platform);
                }
                long? cursor = await store.GetCursorAsync(client.Platform, ct);
                if (client.Platform == Platform.Max)
                {
                    cursor ??= options.InitialMaxMarker;
                }

                PollBatch batch = await client.PollAsync(cursor, ct);
                if (client.Platform == Platform.Max && batch.NextCursor is null && batch.Messages.Count > 0)
                {
                    throw new System.Text.Json.JsonException("MAX не вернул маркер продолжения чтения событий.");
                }

                int count = await store.SaveBatchAsync(client.Platform, destinationChatId, batch, ct);
                failures = 0;
                if (count > 0)
                {
                    logger.LogInformation("{Platform}: queued {Count} message part(s).", client.Platform, count);
                }
                // Prevent busy polling if the API returns immediately (including filtered bot events).
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is ConfigurationException or ApiException { Transient: false })
            {
                StopForConfiguration(client.Platform, ex);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                ApiException or System.Text.Json.JsonException)
            {
                TimeSpan delay = SafeError.Backoff(++failures, (ex as ApiException)?.RetryAfterSeconds);
                logger.LogWarning("{Platform} polling: {Reason}; retry in {Seconds:F0}s.",
                    client.Platform, SafeError.Describe(ex), delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                StopForConfiguration(client.Platform, ex);
                return;
            }
        }
    }

    /// <summary>
    /// Последовательно доставляет очередь одного направления с учётом повторов и ограничения частоты.
    /// </summary>
    /// <param name="source">Платформа, из которой получено сообщение.</param>
    /// <param name="destination">Клиент целевого мессенджера.</param>
    /// <param name="ct">Токен остановки цикла отправки.</param>
    /// <returns>Задача цикла отправки до отмены или неисправимой ошибки.</returns>
    /// <remarks>Ошибка записи результата в SQLite после успешной отправки останавливает цикл, чтобы избежать немедленной повторной отправки.</remarks>
    private async Task RelayAsync(Platform source, IMessengerClient destination, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                OutboxItem? item = await store.GetNextAsync(source, ct);
                if (item is null)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                long wait = item.NextAttemptAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(wait, 1000)), ct);
                    continue;
                }
                string targetId;
                try
                {
                    targetId = await destination.SendAsync(item.DestinationChatId, item.Text, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                    ApiException or System.Text.Json.JsonException)
                {
                    bool permanent = ex is ApiException { Transient: false };
                    TimeSpan delay = SafeError.Backoff(item.Attempts + 1, (ex as ApiException)?.RetryAfterSeconds);
                    await store.MarkErrorAsync(item.Id, SafeError.Describe(ex), delay, permanent, ct);
                    logger.LogWarning("{Source} -> {Target}, queue #{Id}: {Reason}; {Action}.",
                        source, destination.Platform, item.Id, SafeError.Describe(ex),
                        permanent ? "saved as failed; use --retry-failed after fixing" : "retry scheduled");
                    // Respect rate limits even for messages that fail permanently.
                    await Task.Delay(options.SendIntervalMilliseconds, ct);
                    continue;
                }
                // A DB failure after a successful send must stop this sender, not trigger immediate resend.
                await store.MarkSentAsync(item.Id, targetId, ct);
                logger.LogInformation("{Source} -> {Target}: delivered queue #{Id}.", source, destination.Platform, item.Id);
                await Task.Delay(options.SendIntervalMilliseconds, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StopForConfiguration(destination.Platform, ex);
        }
    }
}