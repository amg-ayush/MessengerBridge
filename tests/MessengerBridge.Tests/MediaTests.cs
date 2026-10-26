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

/// <summary>Проверяет передачу файлов между реальными клиентами через подставной HTTP-транспорт.</summary>
public sealed class MediaTests
{
    /// <summary>Проверяет фото без текста, выбор наибольшего размера и скрытую ссылку в подписи.</summary>
    [Fact]
    public void TelegramPhotoAndCaptionLinksArePreserved()
    {
        JsonElement updates = Json("""
            [
              {"update_id":1,"message":{"message_id":10,"chat":{"id":-1},"from":{"is_bot":false,"first_name":"Анна"},
                "photo":[{"file_id":"small","width":100,"height":100},{"file_id":"large","width":800,"height":600,"file_size":123}]}},
              {"update_id":2,"message":{"message_id":11,"chat":{"id":-1},"from":{"is_bot":false,"first_name":"Анна"},
                "caption":"Сайт","caption_entities":[{"type":"text_link","url":"https://example.com/a"}],
                "document":{"file_id":"doc","file_name":"report.pdf","mime_type":"application/pdf","file_size":456}}}
            ]
            """);
        PollBatch batch = TelegramClient.ParseUpdates(updates, -1, null);
        Assert.Equal(3, batch.NextCursor);
        Assert.Equal(2, batch.Messages.Count);
        Assert.Empty(batch.Messages[0].Text);
        MessageAttachment photo = Assert.Single(batch.Messages[0].Attachments!);
        Assert.Equal("large", photo.FileId);
        Assert.Equal("image", photo.Type);
        Assert.Equal(123, photo.Size);
        Assert.Equal("Сайт\nhttps://example.com/a", batch.Messages[1].Text);
        Assert.Equal("report.pdf", Assert.Single(batch.Messages[1].Attachments!).FileName);
        Assert.Equal("🎃 Анна из Телеграм", Assert.Single(MessageFormatter.Format(Platform.Telegram, batch.Messages[0])));
    }

    /// <summary>Проверяет типы Telegram, которые раньше отбрасывались вместе с сообщением.</summary>
    /// <param name="field">Поле исходного медиа.</param>
    /// <param name="type">Ожидаемый тип для передачи.</param>
    [Theory]
    [InlineData("video", "video")]
    [InlineData("video_note", "video")]
    [InlineData("audio", "audio")]
    [InlineData("voice", "voice")]
    [InlineData("animation", "animation")]
    [InlineData("document", "file")]
    [InlineData("sticker", "file")]
    public void TelegramMediaTypesAreRecognized(string field, string type)
    {
        JsonElement message = Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [field] = new { file_id = "source-file", file_size = 20 }
        }));
        MessageAttachment attachment = Assert.Single(MessageContent.TelegramAttachments(message));
        Assert.Equal(type, attachment.Type);
        Assert.Equal("source-file", attachment.FileId);
    }

    /// <summary>Проверяет передачу статического кадра и видеосоставляющей live photo без дублирования кадра совместимости.</summary>
    [Fact]
    public void LivePhotoKeepsPhotoAndVideoOnce()
    {
        JsonElement message = Json("""
            {"live_photo":{"file_id":"live-video","photo":[{"file_id":"small","width":10,"height":10},
              {"file_id":"large","width":100,"height":100}]},
              "photo":[{"file_id":"large","width":100,"height":100}]}
            """);
        IReadOnlyList<MessageAttachment> attachments = MessageContent.TelegramAttachments(message);
        Assert.Equal(2, attachments.Count);
        Assert.Equal("large", attachments[0].FileId);
        Assert.Equal("image", attachments[0].Type);
        Assert.Equal("live-video", attachments[1].FileId);
        Assert.Equal("video", attachments[1].Type);
    }

    /// <summary>Проверяет несколько вложений MAX, ссылки, геопозицию и текстовый резерв для стикера.</summary>
    [Fact]
    public void MaxAttachmentsLinksAndSpecialContentArePreserved()
    {
        JsonElement updates = Json("""
            {"marker":42,"updates":[{"update_type":"message_created","message":{
              "recipient":{"chat_id":-2},"sender":{"is_bot":false,"name":"Борис"},"body":{
                "mid":"mid.1","text":"Фото","markup":[{"type":"link","url":"https://example.com/hidden"}],
                "attachments":[
                  {"type":"image","payload":{"url":"https://cdn.example/a.jpg","token":"a"}},
                  {"type":"file","filename":"data.pdf","size":12,"payload":{"url":"https://cdn.example/data.pdf","token":"b"}},
                  {"type":"share","payload":{"url":"https://example.com/shared"}},
                  {"type":"location","latitude":52.5,"longitude":104.3},
                  {"type":"contact","payload":{"vcf_info":"TEL:+71234567890"}},
                  {"type":"sticker","payload":{"code":"sticker-code"}}
                ]}}}]}
            """);
        PollBatch batch = MaxClient.ParseUpdates(updates, -2);
        IncomingMessage message = Assert.Single(batch.Messages);
        Assert.Equal(42, batch.NextCursor);
        Assert.Equal(2, message.Attachments!.Count);
        Assert.Equal("data.pdf", message.Attachments[1].FileName);
        Assert.Contains("https://example.com/hidden", message.Text);
        Assert.Contains("https://example.com/shared", message.Text);
        Assert.Contains("https://maps.google.com/?q=52.5,104.3", message.Text);
        Assert.Contains("TEL:+71234567890", message.Text);
        Assert.Contains("[Стикер MAX: sticker-code]", message.Text);
    }

    /// <summary>Проверяет миграцию прежней базы, отсутствие повторных вложений и сохранение токена после перезапуска.</summary>
    /// <returns>Задача проверки SQLite.</returns>
    [Fact]
    public async Task MediaQueueMigratesOldDatabaseAndPersistsPreparedToken()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bridge-media-tests-" + Guid.NewGuid());
        string path = Path.Combine(directory, "bridge.db");
        try
        {
            using (BridgeStore store = new(path))
            {
                await store.SaveBatchAsync(Platform.Telegram, -2, new PollBatch(10, [new("old", -1, "A", "old text")]));
            }
            using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE outbox DROP COLUMN attachment";
                command.ExecuteNonQuery();
            }
            PollBatch media = new(11, [new("new", -1, "A", new string('x', 2400) + "😀",
                [new("image", FileId: "photo"), new("file", FileId: "document", FileName: "a.pdf")])]);
            long firstMediaId;
            using (BridgeStore store = new(path))
            {
                OutboxItem old = (await store.GetNextAsync(Platform.Telegram))!;
                Assert.Equal("🎃 A из Телеграм\n\nold text", old.Text);
                Assert.Null(old.Attachment);
                await store.MarkSentAsync(old.Id, "old-target");
                Assert.Equal(4, await store.SaveBatchAsync(Platform.Telegram, -2, media));
                Assert.Equal(0, await store.SaveBatchAsync(Platform.Telegram, -2, media));
                OutboxItem first = (await store.GetNextAsync(Platform.Telegram))!;
                firstMediaId = first.Id;
                Assert.Equal("photo", first.Attachment!.FileId);
                Assert.InRange(first.Text.Length, 1, 1000);
                await store.SetAttachmentAsync(first.Id, first.Attachment with { UploadedToken = "uploaded" });
            }
            using (BridgeStore store = new(path))
            {
                Assert.Equal(11, await store.GetCursorAsync(Platform.Telegram));
                OutboxItem first = (await store.GetNextAsync(Platform.Telegram))!;
                Assert.Equal(firstMediaId, first.Id);
                Assert.Equal("uploaded", first.Attachment!.UploadedToken);
                StringBuilder combined = new();
                for (int i = 0; i < 3; i++)
                {
                    OutboxItem item = (await store.GetNextAsync(Platform.Telegram))!;
                    combined.Append(item.Text["🎃 A из Телеграм\n\n".Length..]);
                    await store.MarkSentAsync(item.Id, "target-" + i);
                }
                Assert.Equal(media.Messages[0].Text, combined.ToString());
                OutboxItem secondFile = (await store.GetNextAsync(Platform.Telegram))!;
                Assert.Equal("document", secondFile.Attachment!.FileId);
                Assert.Equal("🎃 A из Телеграм", secondFile.Text);
                await store.MarkSentAsync(secondFile.Id, "document-target");
                Assert.Null(await store.GetNextAsync(Platform.Telegram));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Проверяет полную передачу фото Telegram в MAX и повтор без повторной загрузки при обработке вложения.</summary>
    /// <returns>Задача проверки HTTP-запросов.</returns>
    [Fact]
    public async Task TelegramPhotoUploadsToMaxAndRetriesUsingSameToken()
    {
        byte[] bytes = [1, 2, 3, 4];
        using FakeHandler telegramHandler = new(request =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath == "/bot1:fake/getFile")
            {
                return Task.FromResult(Response("""{"ok":true,"result":{"file_path":"photos/a.jpg"}}"""));
            }
            Assert.Equal("/file/bot1:fake/photos/a.jpg", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        int uploads = 0;
        int sends = 0;
        using FakeHandler maxHandler = new(async request =>
        {
            if (request.RequestUri!.Host == "upload.example")
            {
                uploads++;
                Assert.False(request.Headers.Contains("Authorization"));
                MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
                HttpContent content = Assert.Single(form);
                Assert.Equal("data", content.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(bytes, await content.ReadAsByteArrayAsync());
                return Response("""{"photos":{"photo-id":{"token":"uploaded-photo"}}}""");
            }
            Assert.Equal("max-secret", Assert.Single(request.Headers.GetValues("Authorization")));
            if (request.RequestUri.AbsolutePath == "/uploads")
            {
                Assert.Equal("?type=image", request.RequestUri.Query);
                return Response("""{"url":"https://upload.example/photo"}""");
            }
            JsonElement body = Json(await request.Content!.ReadAsStringAsync());
            Assert.Equal("[Telegram] Анна", body.GetProperty("text").GetString());
            Assert.Equal("image", body.GetProperty("attachments")[0].GetProperty("type").GetString());
            Assert.Equal("uploaded-photo", body.GetProperty("attachments")[0].GetProperty("payload").GetProperty("token").GetString());
            return ++sends == 1
                ? Response("""{"code":"attachment.not.ready","message":"sensitive-content"}""", HttpStatusCode.BadRequest)
                : Response("""{"message":{"body":{"mid":"sent-photo"}}}""");
        });
        BridgeOptions options = new()
        {
            Telegram = new EndpointOptions { Token = "1:fake" },
            Max = new EndpointOptions { Token = "max-secret" }
        };
        using TelegramClient telegram = new(new HttpClient(telegramHandler) { BaseAddress = new Uri("https://telegram.example/") }, options);
        using MaxClient max = new(new HttpClient(maxHandler) { BaseAddress = new Uri("https://max.example/") }, options);
        MessageAttachment prepared = await max.PrepareAttachmentAsync(new("image", FileId: "photo", FileName: "a.jpg"), telegram, default);
        ApiException error = await Assert.ThrowsAsync<ApiException>(() => max.SendAttachmentAsync(-2, "[Telegram] Анна", prepared, telegram, default));
        Assert.True(error.Transient);
        Assert.DoesNotContain("sensitive-content", error.ToString());
        Assert.Equal(prepared, await max.PrepareAttachmentAsync(prepared, telegram, default));
        Assert.Equal("sent-photo", await max.SendAttachmentAsync(-2, "[Telegram] Анна", prepared, telegram, default));
        Assert.Equal(1, uploads);
        Assert.Equal(2, sends);
    }

    /// <summary>Проверяет XML-ответы загрузки аудио и видео, а также JSON-токен произвольного файла MAX.</summary>
    /// <param name="type">Исходный тип Telegram.</param>
    /// <param name="uploadType">Тип загрузки MAX.</param>
    /// <returns>Задача проверки этапов загрузки.</returns>
    [Theory]
    [InlineData("voice", "audio")]
    [InlineData("audio", "audio")]
    [InlineData("video", "video")]
    [InlineData("animation", "video")]
    [InlineData("file", "file")]
    public async Task MaxAudioVideoAndFileUploadsUseDocumentedTokens(string type, string uploadType)
    {
        using FakeHandler telegramHandler = new(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("getFile", StringComparison.Ordinal)
                ? Response("""{"ok":true,"result":{"file_path":"media/file"}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([3, 2, 1]) }));
        using FakeHandler maxHandler = new(async request =>
        {
            if (request.RequestUri!.Host == "upload.example")
            {
                Assert.False(request.Headers.Contains("Authorization"));
                MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.Equal(new byte[] { 3, 2, 1 }, await Assert.Single(form).ReadAsByteArrayAsync());
                return Response(uploadType == "file" ? """{"fileId":99,"token":"ready"}""" : "<retval>1</retval>");
            }
            Assert.Equal("?type=" + uploadType, request.RequestUri.Query);
            return Response(uploadType == "file" ? """{"url":"https://upload.example/file"}""" :
                """{"url":"https://upload.example/media","token":"ready"}""");
        });
        BridgeOptions options = new()
        {
            Telegram = new EndpointOptions { Token = "1:fake" },
            Max = new EndpointOptions { Token = "max-secret" }
        };
        using TelegramClient telegram = new(new HttpClient(telegramHandler) { BaseAddress = new Uri("https://telegram.example/") }, options);
        using MaxClient max = new(new HttpClient(maxHandler) { BaseAddress = new Uri("https://max.example/") }, options);
        MessageAttachment prepared = await max.PrepareAttachmentAsync(new(type, FileId: "media"), telegram, default);
        Assert.Equal(uploadType, prepared.Type);
        Assert.Equal("ready", prepared.UploadedToken);
    }

    /// <summary>Проверяет скачивание MAX и multipart-отправку основных типов в Telegram с сохранением подписи и байтов.</summary>
    /// <param name="type">Тип исходного вложения.</param>
    /// <param name="method">Ожидаемый метод Telegram.</param>
    /// <param name="field">Ожидаемое multipart-поле файла.</param>
    /// <param name="fileName">Имя исходного файла либо null для выбора по типу.</param>
    /// <returns>Задача проверки передачи.</returns>
    [Theory]
    [InlineData("image", "sendPhoto", "photo", null)]
    [InlineData("video", "sendVideo", "video", null)]
    [InlineData("audio", "sendAudio", "audio", null)]
    [InlineData("file", "sendDocument", "document", null)]
    [InlineData("audio", "sendVoice", "voice", "voice.ogg")]
    [InlineData("video", "sendDocument", "document", "video.mkv")]
    [InlineData("image", "sendDocument", "document", "image.heic")]
    public async Task MaxFilesUploadToTelegramWithCaption(string type, string method, string field, string? fileName)
    {
        byte[] bytes = [9, 8, 7];
        using FakeHandler maxHandler = new(request =>
        {
            if (request.RequestUri!.Host == "cdn.example")
            {
                Assert.False(request.Headers.Contains("Authorization"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            Assert.Equal("max-secret", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.Equal("/videos/video-token", request.RequestUri.AbsolutePath);
            return Task.FromResult(Response("""{"urls":{"mp4_720":"https://cdn.example/video.mp4"}}"""));
        });
        using FakeHandler telegramHandler = new(async request =>
        {
            Assert.Equal("/bot1:fake/" + method, request.RequestUri!.AbsolutePath);
            MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Dictionary<string, HttpContent> parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("-1", await parts["chat_id"].ReadAsStringAsync());
            Assert.Equal("[MAX] Борис\nПодпись https://example.com", await parts["caption"].ReadAsStringAsync());
            Assert.Equal(bytes, await parts[field].ReadAsByteArrayAsync());
            return Response("""{"ok":true,"result":{"message_id":123}}""");
        });
        BridgeOptions options = new()
        {
            Telegram = new EndpointOptions { Token = "1:fake" },
            Max = new EndpointOptions { Token = "max-secret" }
        };
        using MaxClient max = new(new HttpClient(maxHandler) { BaseAddress = new Uri("https://max.example/") }, options);
        using TelegramClient telegram = new(new HttpClient(telegramHandler) { BaseAddress = new Uri("https://telegram.example/") }, options);
        MessageAttachment attachment = new(type, Url: "https://cdn.example/file", FileName: fileName, Token: type == "video" ? "video-token" : null);
        Assert.Equal("123", await telegram.SendAttachmentAsync(-1, "[MAX] Борис\nПодпись https://example.com", attachment, max, default));
    }

    /// <summary>Проверяет обновление истёкшей ссылки по сообщению MAX, выбор нужного файла и удаление временного файла.</summary>
    /// <returns>Задача проверки загрузки.</returns>
    [Fact]
    public async Task MaxDownloadRefreshesMatchingAttachmentAndDeletesTemporaryFile()
    {
        using FakeHandler handler = new(request =>
        {
            if (request.RequestUri!.Host == "max.example")
            {
                Assert.Equal("/messages?message_ids=mid.old", request.RequestUri.PathAndQuery);
                Assert.Equal("max-secret", Assert.Single(request.Headers.GetValues("Authorization")));
                return Task.FromResult(Response("""
                    {"messages":[{"body":{"mid":"mid.old","attachments":[
                      {"type":"image","payload":{"url":"https://cdn.example/first.jpg","token":"a"}},
                      {"type":"image","payload":{"url":"https://cdn.example/fresh.jpg","token":"b"}}]}}]}
                    """));
            }
            Assert.Equal("https://cdn.example/fresh.jpg", request.RequestUri.AbsoluteUri);
            Assert.False(request.Headers.Contains("Authorization"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([5, 6]) });
        });
        using MaxClient max = new(new HttpClient(handler) { BaseAddress = new Uri("https://max.example/") },
            new BridgeOptions { Max = new EndpointOptions { Token = "max-secret" } });
        string temporaryPath;
        using (MediaFile file = await max.DownloadAsync(new("image", Url: "https://expired.example/old.jpg", Token: "b", MessageId: "mid.old"), default))
        {
            temporaryPath = Assert.IsType<FileStream>(file.Content).Name;
            Assert.True(File.Exists(temporaryPath));
            Assert.Equal("fresh.jpg", file.FileName);
            byte[] bytes = new byte[2];
            Assert.Equal(2, await file.Content.ReadAsync(bytes));
            Assert.Equal(new byte[] { 5, 6 }, bytes);
        }
        Assert.False(File.Exists(temporaryPath));
    }

    /// <summary>Проверяет ограничение размеров до сетевого запроса и запрет небезопасных URL.</summary>
    /// <returns>Задача проверки ограничений.</returns>
    [Fact]
    public async Task OversizedAndUnsafeAttachmentsAreRejectedWithoutNetwork()
    {
        using FakeHandler handler = new(_ => throw new InvalidOperationException("Сетевой запрос не должен выполняться."));
        using HttpClient http = new(handler);
        await Assert.ThrowsAsync<AttachmentException>(() => MediaTransfer.DownloadAsync(http, "https://cdn.example/file",
            new("file", Size: MediaTransfer.MaxBytes + 1), default));
        using TelegramClient telegram = new(new HttpClient(handler) { BaseAddress = new Uri("https://telegram.example/") },
            new BridgeOptions { Telegram = new EndpointOptions { Token = "1:fake" } });
        await Assert.ThrowsAsync<AttachmentException>(() => telegram.DownloadAsync(new("file", FileId: "large", Size: 20L * 1024 * 1024 + 1), default));
        Assert.Throws<AttachmentException>(() => MediaTransfer.Address("http://cdn.example/file"));
        Assert.Throws<AttachmentException>(() => MediaTransfer.Address("https://localhost/file"));
        Assert.Throws<AttachmentException>(() => MediaTransfer.Address("https://secret@cdn.example/file"));
    }

    /// <summary>Создаёт JSON-ответ подставного сервера.</summary>
    /// <param name="json">Тело ответа.</param>
    /// <param name="status">HTTP-статус.</param>
    /// <returns>Ответ с JSON-содержимым.</returns>
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json) };

    /// <summary>Разбирает тестовый JSON с независимым временем жизни.</summary>
    /// <param name="json">Тестовый JSON.</param>
    /// <returns>Копия корневого элемента.</returns>
    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}