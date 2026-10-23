using MessengerBridge.Clients;
using MessengerBridge.Configuration;
using MessengerBridge.Persistence;

namespace MessengerBridge.Workers;

public sealed class BridgeWorker(TelegramClient telegram, MaxClient max, BridgeStore store,
    BridgeOptions options, ILogger<BridgeWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
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

    private void StopForConfiguration(Platform platform, Exception ex)
    {
        logger.LogCritical("{Platform}: {Reason}. Fix configuration and restart.",
            platform, SafeError.Describe(ex));
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    private async Task ReceiveAsync(IMessengerClient client, long destinationChatId, CancellationToken ct)
    {
        var verified = false;
        var failures = 0;
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
                var cursor = await store.GetCursorAsync(client.Platform, ct);
                if (client.Platform == Platform.Max) cursor ??= options.InitialMaxMarker;
                PollBatch batch = await client.PollAsync(cursor, ct);
                if (client.Platform == Platform.Max && batch.NextCursor is null && batch.Messages.Count > 0)
                    throw new System.Text.Json.JsonException("MAX returned no continuation marker.");
                var count = await store.SaveBatchAsync(client.Platform, destinationChatId, batch, ct);
                failures = 0;
                if (count > 0) logger.LogInformation("{Platform}: queued {Count} message part(s).", client.Platform, count);
                // Prevent busy polling if the API returns immediately (including filtered bot events).
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is ConfigurationException || ex is ApiException { Transient: false })
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
                var wait = item.NextAttemptAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                    var permanent = ex is ApiException { Transient: false };
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