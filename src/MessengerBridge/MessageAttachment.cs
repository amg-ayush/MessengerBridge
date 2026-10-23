namespace MessengerBridge;

/// <summary>
/// Описание исходного медиафайла и результата его подготовки на целевой платформе.
/// </summary>
/// <param name="Type">Тип: image, video, audio, voice, animation или file.</param>
/// <param name="FileId">Идентификатор файла Telegram для получения свежей ссылки.</param>
/// <param name="Url">Ссылка на файл MAX, не содержащая токена бота.</param>
/// <param name="FileName">Исходное имя файла.</param>
/// <param name="Size">Размер файла в байтах, если известен.</param>
/// <param name="ContentType">MIME-тип файла.</param>
/// <param name="Token">Токен исходного видео MAX.</param>
/// <param name="MessageId">Идентификатор исходного сообщения MAX для обновления ссылки.</param>
/// <param name="UploadedToken">Сохранённый токен файла, загруженного в целевой MAX.</param>
public sealed record MessageAttachment(string Type, string? FileId = null, string? Url = null,
    string? FileName = null, long? Size = null, string? ContentType = null, string? Token = null,
    string? MessageId = null, string? UploadedToken = null);