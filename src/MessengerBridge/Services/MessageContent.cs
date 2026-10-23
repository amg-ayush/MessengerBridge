using System.Globalization;
using System.Text.Json;
using MessengerBridge.Clients;

namespace MessengerBridge.Services;

/// <summary>Извлекает медиа, подписи и ссылки без потери нетекстового содержимого.</summary>
public static class MessageContent
{
    /// <summary>Читает подпись, скрытые ссылки и текстовые представления специальных сообщений Telegram.</summary>
    /// <param name="message">Объект сообщения Telegram.</param>
    /// <returns>Текст, пригодный для пересылки без привязки к разметке платформы.</returns>
    public static string TelegramText(JsonElement message)
    {
        string text = message.Text("text") ?? message.Text("caption") ?? "";
        foreach (string field in new[] { "entities", "caption_entities" })
        {
            if (message.TryGetProperty(field, out JsonElement entities) && entities.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entity in entities.EnumerateArray())
                {
                    if (entity.Text("type") == "text_link")
                    {
                        text = AddLink(text, entity.Text("url"));
                    }
                }
            }
        }
        if (message.TryGetProperty("contact", out JsonElement contact))
        {
            text += $"\nКонтакт: {contact.Text("first_name")} {contact.Text("last_name")}\n{contact.Text("phone_number")}\n{contact.Text("vcard")}";
        }
        JsonElement location;
        if (message.TryGetProperty("venue", out JsonElement venue))
        {
            text += $"\n{venue.Text("title")}\n{venue.Text("address")}";
            venue.TryGetProperty("location", out location);
        }
        else
        {
            message.TryGetProperty("location", out location);
        }
        text = AddLocation(text, location);
        if (message.TryGetProperty("poll", out _))
        {
            text += "\n" + PollTextFormatter.FormatTelegram(message);
        }
        if (message.TryGetProperty("dice", out JsonElement dice))
        {
            text += $"\n{dice.Text("emoji")} {dice.GetProperty("value")}";
        }
        if (message.TryGetProperty("reply_markup", out JsonElement keyboard) &&
            keyboard.TryGetProperty("inline_keyboard", out JsonElement rows))
        {
            text = AddButtons(text, rows);
        }
        return text.Trim();
    }

    /// <summary>Выбирает исходный файл Telegram, включая наибольший размер фотографии.</summary>
    /// <param name="message">Объект сообщения Telegram.</param>
    /// <returns>Описание файла либо пустой список для текстового сообщения.</returns>
    public static IReadOnlyList<MessageAttachment> TelegramAttachments(JsonElement message)
    {
        if (message.TryGetProperty("live_photo", out JsonElement live))
        {
            List<MessageAttachment> files = [];
            if (live.TryGetProperty("photo", out JsonElement photo) && photo.ValueKind == JsonValueKind.Array && photo.GetArrayLength() > 0)
            {
                files.Add(TelegramFile(photo.EnumerateArray().OrderBy(p => Number(p, "width") * Number(p, "height")).Last(),
                    "image", "live-photo.jpg", "image/jpeg"));
            }
            if (live.Text("file_id") is not null)
            {
                files.Add(TelegramFile(live, "video", "live-photo.mp4", "video/mp4"));
            }
            return files;
        }
        if (message.TryGetProperty("photo", out JsonElement photos) && photos.GetArrayLength() > 0)
        {
            return [TelegramFile(photos.EnumerateArray().OrderBy(p => Number(p, "width") * Number(p, "height")).Last(), "image", "photo.jpg", "image/jpeg")];
        }
        foreach ((string field, string type, string name, string mime) in new[]
        {
            ("animation", "animation", "animation.mp4", "video/mp4"),
            ("video", "video", "video.mp4", "video/mp4"),
            ("video_note", "video", "video-note.mp4", "video/mp4"),
            ("audio", "audio", "audio.mp3", "audio/mpeg"),
            ("voice", "voice", "voice.ogg", "audio/ogg"),
            ("sticker", "file", "sticker.webp", "image/webp"),
            ("document", "file", "document.bin", "application/octet-stream")
        })
        {
            if (message.TryGetProperty(field, out JsonElement file))
            {
                if (field == "sticker")
                {
                    if (file.TryGetProperty("is_animated", out JsonElement animated) && animated.ValueKind == JsonValueKind.True)
                    {
                        return [TelegramFile(file, "file", "sticker.tgs", "application/x-tgsticker")];
                    }
                    if (file.TryGetProperty("is_video", out JsonElement video) && video.ValueKind == JsonValueKind.True)
                    {
                        return [TelegramFile(file, "video", "sticker.webm", "video/webm")];
                    }
                }
                return [TelegramFile(file, type, name, mime)];
            }
        }
        return [];
    }

    /// <summary>Читает текст MAX, скрытые ссылки, контакты, геопозиции и пояснения неподдерживаемых вложений.</summary>
    /// <param name="body">Объект содержимого сообщения MAX.</param>
    /// <returns>Текст сообщения с доступными внешними ссылками.</returns>
    public static string MaxText(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return "[Пересланное сообщение без доступного содержимого]";
        }
        string text = body.Text("text") ?? "";
        if (body.TryGetProperty("markup", out JsonElement markup) && markup.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in markup.EnumerateArray())
            {
                text = AddLink(text, item.Text("url"));
            }
        }
        foreach (JsonElement item in Attachments(body))
        {
            string? type = item.Text("type");
            JsonElement payload = item.TryGetProperty("payload", out JsonElement value) ? value : item;
            switch (type)
            {
                case "share":
                    text = AddLink(text, payload.Text("url"));
                    break;
                case "location":
                    text = AddLocation(text, item.TryGetProperty("latitude", out _) ? item : payload);
                    break;
                case "contact":
                    text += $"\nКонтакт: {payload.Text("name")}\n{payload.Text("vcf_info")}";
                    if (payload.TryGetProperty("max_info", out JsonElement user))
                    {
                        text += "\n" + user.Author();
                    }
                    break;
                case "inline_keyboard":
                    if (payload.TryGetProperty("buttons", out JsonElement buttons))
                    {
                        text = AddButtons(text, buttons);
                    }
                    break;
                case "sticker" when string.IsNullOrWhiteSpace(payload.Text("url")):
                    text += $"\n[Стикер MAX: {payload.Text("code")}]";
                    break;
                case "poll":
                    text += "\n[Опрос MAX: публичный API не описывает перенос нативного опроса и голосов. Откройте исходный чат MAX для голосования.]";
                    break;
                case "image" or "video" or "audio" or "file" or "sticker":
                    break;
                default:
                    text += $"\n[Вложение MAX: {type ?? "неизвестный тип"}]";
                    break;
            }
        }
        return text.Trim();
    }

    /// <summary>Извлекает все доступные файлы MAX; видео сохраняет токен для получения MP4.</summary>
    /// <param name="body">Содержимое сообщения MAX.</param>
    /// <returns>Упорядоченный список медиафайлов.</returns>
    public static IReadOnlyList<MessageAttachment> MaxAttachments(JsonElement body)
    {
        List<MessageAttachment> result = [];
        foreach (JsonElement item in Attachments(body))
        {
            string? type = item.Text("type");
            if (type is not ("image" or "video" or "audio" or "file" or "sticker") ||
                !item.TryGetProperty("payload", out JsonElement payload))
            {
                continue;
            }
            string? url = payload.Text("url");
            if (type == "sticker" && string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            string kind = type == "sticker" ? "file" : type;
            string? fileName = item.Text("filename");
            result.Add(new MessageAttachment(kind, Url: url, FileName: fileName, Size: OptionalNumber(item, "size"),
                Token: payload.Text("token"), MessageId: body.Text("mid")));
        }
        return result;
    }

    /// <summary>Создаёт описание файла Telegram с именем, типом и размером.</summary>
    /// <param name="file">JSON-объект файла.</param>
    /// <param name="type">Нормализованный тип вложения.</param>
    /// <param name="name">Имя по умолчанию.</param>
    /// <param name="mime">MIME-тип по умолчанию.</param>
    /// <returns>Описание для последующего вызова getFile.</returns>
    private static MessageAttachment TelegramFile(JsonElement file, string type, string name, string mime) =>
        new(type, FileId: file.Text("file_id"), FileName: file.Text("file_name") ?? name,
            Size: OptionalNumber(file, "file_size"), ContentType: file.Text("mime_type") ?? mime);

    /// <summary>Возвращает массив вложений либо пустой список.</summary>
    /// <param name="body">Содержимое MAX.</param>
    /// <returns>Вложения без изменения исходного JSON.</returns>
    private static IEnumerable<JsonElement> Attachments(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("attachments", out JsonElement array) &&
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    /// <summary>Читает необязательное целое число.</summary>
    /// <param name="item">JSON-объект.</param>
    /// <param name="field">Имя поля.</param>
    /// <returns>Число либо null.</returns>
    private static long? OptionalNumber(JsonElement item, string field) =>
        item.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number) ? number : null;

    /// <summary>Читает размер изображения для выбора наибольшего варианта.</summary>
    /// <param name="item">JSON-объект изображения.</param>
    /// <param name="field">Ширина или высота.</param>
    /// <returns>Значение поля либо 0.</returns>
    private static long Number(JsonElement item, string field) => OptionalNumber(item, field) ?? 0;

    /// <summary>Дописывает скрытую ссылку, если она ещё не присутствует в тексте.</summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="url">URL либо null.</param>
    /// <returns>Текст с доступным адресом ссылки.</returns>
    private static string AddLink(string text, string? url) => string.IsNullOrWhiteSpace(url) || text.Contains(url, StringComparison.Ordinal)
        ? text : text + "\n" + url;

    /// <summary>Дописывает географические координаты и ссылку на карту.</summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="location">Объект с latitude и longitude.</param>
    /// <returns>Текст с местоположением.</returns>
    private static string AddLocation(string text, JsonElement location)
    {
        if (location.ValueKind != JsonValueKind.Object || !location.TryGetProperty("latitude", out JsonElement latitude) ||
            !location.TryGetProperty("longitude", out JsonElement longitude))
        {
            return text;
        }
        string lat = latitude.GetDouble().ToString(CultureInfo.InvariantCulture);
        string lon = longitude.GetDouble().ToString(CultureInfo.InvariantCulture);
        return text + $"\nГеопозиция: {lat}, {lon}\nhttps://maps.google.com/?q={lat},{lon}";
    }

    /// <summary>Сохраняет внешние ссылки кнопок без переноса действий бота.</summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="rows">Двумерный массив кнопок.</param>
    /// <returns>Текст с адресами кнопок.</returns>
    private static string AddButtons(string text, JsonElement rows)
    {
        foreach (JsonElement row in rows.EnumerateArray())
        {
            foreach (JsonElement button in row.EnumerateArray())
            {
                text = AddLink(text, button.Text("url"));
            }
        }
        return text;
    }
}