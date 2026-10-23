using System.Net;
using System.Text;
using System.Text.Json;
using MessengerBridge.Clients;
using MessengerBridge.Configuration;
using MessengerBridge.Persistence;
using MessengerBridge.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessengerBridge.Tests;

public sealed class BridgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid());
    private string Db => Path.Combine(_directory, "test.db");

    [Fact]
    public void TelegramFiltersBotsOtherGroupsAndAnonymousSendersButAdvancesOffset()
    {
        JsonElement root = Json("""
            [
              {"update_id":10,"message":{"message_id":1,"chat":{"id":-5369085414},"from":{"is_bot":false,"first_name":"Amgalan"},"text":"Виски и Пепси"}},
              {"update_id":11,"message":{"message_id":2,"chat":{"id":-5369085414},"from":{"is_bot":true},"text":"echo"}},
              {"update_id":12,"message":{"message_id":3,"chat":{"id":99},"from":{"is_bot":false},"text":"private"}},
              {"update_id":13,"edited_message":{"text":"ignored"}},
              {"update_id":14,"message":{"message_id":4,"chat":{"id":-5369085414},"sender_chat":{"id":-10099},"from":{"is_bot":false},"text":"anonymous"}},
              {"update_id":15,"message":{"message_id":5,"chat":{"id":-5369085414},"from":{"is_bot":false},"photo":[{}]}}
            ]
            """);
        PollBatch batch = TelegramClient.ParseUpdates(root, -5369085414, null);
        Assert.Equal(16, batch.NextCursor);
        IncomingMessage message = Assert.Single(batch.Messages);
        Assert.Equal("Виски и Пепси", message.Text);
        Assert.Equal("Amgalan", message.Author);
    }

    [Fact]
    public void MaxParsesInt64ChatAndSkipsBotEchoes()
    {
        JsonElement root = Json("""
            {"updates":[
              {"update_type":"message_created","message":{"recipient":{"chat_id":-80442349966802},"sender":{"is_bot":false,"first_name":"Amgalan","last_name":"Ayush"},"body":{"mid":"mid.1","text":"Пепси"}}},
              {"update_type":"message_created","message":{"recipient":{"chat_id":-80442349966802},"sender":{"is_bot":true},"body":{"mid":"mid.2","text":"echo"}}},
              {"update_type":"message_created","message":{"recipient":{"chat_id":1},"sender":{"is_bot":false},"body":{"mid":"mid.3","text":"elsewhere"}}},
              {"update_type":"message_removed"}
            ],"marker":83040}
            """);
        PollBatch batch = MaxClient.ParseUpdates(root, -80442349966802);
        Assert.Equal(83040, batch.NextCursor);
        Assert.Equal("Amgalan Ayush", Assert.Single(batch.Messages).Author);
    }

    [Fact]
    public void LongUnicodeMessageIsSplitWithoutLosingTextOrBreakingSurrogates()
    {
        string text = string.Concat(Enumerable.Repeat("Пепси 🐈‍⬛ ", 2000));
        IReadOnlyList<string> parts = MessageFormatter.Format(Platform.Max, new("mid", -1, "Amgalan", text));
        const string header = "[MAX] Amgalan\n";
        Assert.True(parts.Count > 1);
        Assert.Equal(text, string.Concat(parts.Select(p => p[header.Length..])));
        foreach (string part in parts)
        {
            Assert.InRange(part.Length, 1, MessageFormatter.MaxLength);
            _ = new UTF8Encoding(false, true).GetBytes(part);
        }
    }

    [Fact]
    public async Task QueueAndCursorSurviveRestartAndRepeatedBatchDoesNotResend()
    {
        PollBatch batch = new(42, [new("message-1", -1, "Amgalan", "Пепси")]);
        long id;
        using (BridgeStore store = new(Db))
        {
            Assert.Equal(1, await store.SaveBatchAsync(Platform.Telegram, -2, batch));
            id = (await store.GetNextAsync(Platform.Telegram))!.Id;
        }
        using (BridgeStore store = new(Db))
        {
            Assert.Equal(42, await store.GetCursorAsync(Platform.Telegram));
            Assert.Equal(id, (await store.GetNextAsync(Platform.Telegram))!.Id);
            Assert.Equal(0, await store.SaveBatchAsync(Platform.Telegram, -2, batch));
            await store.MarkSentAsync(id, "target-1");
        }
        using (BridgeStore store = new(Db))
        {
            Assert.Equal(0, await store.SaveBatchAsync(Platform.Telegram, -2, batch));
            Assert.Null(await store.GetNextAsync(Platform.Telegram));
        }
    }

    [Fact]
    public async Task FailedBatchRollsBackBothQueueAndCursor()
    {
        using BridgeStore store = new(Db);
        await store.SaveBatchAsync(Platform.Max, -2, new PollBatch(100, []));
        using (SqliteConnection connection = new($"Data Source={Db};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TRIGGER reject_bad BEFORE INSERT ON outbox
                WHEN NEW.source_message_id='bad' BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;
                """;
            cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveBatchAsync(Platform.Max, -2,
            new PollBatch(200, [new("ok", -1, "A", "first"), new("bad", -1, "B", "second")])));
        Assert.Equal(100, await store.GetCursorAsync(Platform.Max));
        Assert.Null(await store.GetNextAsync(Platform.Max));
    }

    [Fact]
    public async Task RetryKeepsHeadOfQueueAndFailureCanBeExplicitlyRequeued()
    {
        using BridgeStore store = new(Db);
        await store.SaveBatchAsync(Platform.Max, -2,
            new PollBatch(1, [new("1", -1, "A", "first"), new("2", -1, "A", "second")]));
        OutboxItem first = (await store.GetNextAsync(Platform.Max))!;
        await store.MarkErrorAsync(first.Id, "Network", TimeSpan.FromMinutes(1), false);
        OutboxItem retry = (await store.GetNextAsync(Platform.Max))!;
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, retry.Attempts);
        Assert.True(retry.NextAttemptAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await store.MarkErrorAsync(first.Id, "API 403", TimeSpan.Zero, true);
        Assert.NotEqual(first.Id, (await store.GetNextAsync(Platform.Max))!.Id);
        Assert.Equal(1, await store.RetryFailedAsync(-99, -88));
        OutboxItem requeued = (await store.GetNextAsync(Platform.Max))!;
        Assert.Equal(first.Id, requeued.Id);
        Assert.Equal(-99, requeued.DestinationChatId);
    }

    [Fact]
    public async Task IndependentDirectionsAndSourceChatIdsDoNotCollide()
    {
        using BridgeStore store = new(Db);
        await Task.WhenAll(
            store.SaveBatchAsync(Platform.Max, -2, new PollBatch(10, [new("1", -1, "A", "a")])),
            store.SaveBatchAsync(Platform.Telegram, -1, new PollBatch(20, [new("1", -2, "B", "b")])));
        Assert.NotNull(await store.GetNextAsync(Platform.Max));
        Assert.NotNull(await store.GetNextAsync(Platform.Telegram));
        Assert.Equal(1, await store.SaveBatchAsync(Platform.Max, -2,
            new PollBatch(11, [new("1", -99, "C", "c")])));
    }

    [Fact]
    public async Task TelegramHonorsRetryAfterWithoutExposingResponseOrToken()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""
                {"ok":false,"description":"sensitive-value","parameters":{"retry_after":35}}
                """)
        };
        ApiException error = await Assert.ThrowsAsync<ApiException>(() => ApiTransport.ReadAsync(response, default));
        Assert.True(error.Transient);
        Assert.Equal(35, error.RetryAfterSeconds);
        Assert.DoesNotContain("sensitive-value", error.ToString());
        Assert.True(SafeError.Backoff(1, error.RetryAfterSeconds).TotalSeconds >= 35);
    }

    [Fact]
    public async Task MigrationResponseReportsInt64NewChatId()
    {
        using HttpResponseMessage response = new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""
                {"ok":false,"parameters":{"migrate_to_chat_id":-1005369085414}}
                """)
        };
        ApiException error = await Assert.ThrowsAsync<ApiException>(() => ApiTransport.ReadAsync(response, default));
        Assert.False(error.Transient);
        Assert.Equal(-1005369085414, error.MigratedChatId);
    }

    [Fact]
    public async Task TelegramPollAndSendHaveCorrectWireShapeAndPlainText()
    {
        List<(string Path, JsonElement Body)> requests = [];
        using FakeHandler handler = new(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                Json(await request.Content!.ReadAsStringAsync())));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(requests.Count == 1
                    ? "{\"ok\":true,\"result\":[]}" : "{\"ok\":true,\"result\":{\"message_id\":7}}")
            };
        });
        using TelegramClient client = new(new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") },
            new BridgeOptions { Telegram = new EndpointOptions { Token = "1:fake", ChatId = -5369085414 } });
        await client.PollAsync(123, default);
        Assert.Equal(123, requests[0].Body.GetProperty("offset").GetInt64());
        Assert.Equal("message", requests[0].Body.GetProperty("allowed_updates")[0].GetString());
        Assert.Equal("7", await client.SendAsync(-5369085414, "<b>Пепси</b>", default));
        Assert.Equal("/bot1:fake/sendMessage", requests[1].Path);
        Assert.False(requests[1].Body.TryGetProperty("parse_mode", out _));
        Assert.Equal(-5369085414, requests[1].Body.GetProperty("chat_id").GetInt64());
    }

    [Fact]
    public async Task MaxUsesAuthorizationHeaderAndPersistedMarker()
    {
        List<string> requests = [];
        using FakeHandler handler = new(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal("fake-token", Assert.Single(request.Headers.GetValues("Authorization")));
            string result = request.Method == HttpMethod.Get
                ? "{\"updates\":[],\"marker\":83041}"
                : "{\"message\":{\"body\":{\"mid\":\"mid.sent\"}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) });
        });
        using MaxClient client = new(new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") },
            new BridgeOptions { Max = new EndpointOptions { Token = "fake-token", ChatId = -80442349966802 } });
        Assert.Equal(83041, (await client.PollAsync(83040, default)).NextCursor);
        Assert.Contains("marker=83040", requests[0]);
        Assert.Equal("mid.sent", await client.SendAsync(-80442349966802, "Пепси", default));
        Assert.Equal("/messages?chat_id=-80442349966802", requests[1]);
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

}