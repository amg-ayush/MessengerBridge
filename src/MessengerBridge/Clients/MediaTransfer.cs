using System.Net;

namespace MessengerBridge.Clients;

/// <summary>
/// Потоково загружает медиа на временный диск с ограничением размера и проверкой HTTPS.
/// </summary>
public static class MediaTransfer
{
    /// <summary>Предел загрузки файлов в облачный Telegram Bot API: 50 МиБ.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Проверяет абсолютный HTTPS-адрес без встроенных учётных данных.</summary>
    /// <param name="url">Ссылка, полученная от API мессенджера.</param>
    /// <returns>Проверенный адрес файла.</returns>
    /// <exception cref="AttachmentException">Ссылка отсутствует или имеет недопустимый формат.</exception>
    public static Uri Address(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme != "https" ||
            address.UserInfo.Length != 0 || address.IsLoopback)
        {
            throw new AttachmentException("API не предоставил допустимую HTTPS-ссылку на вложение.");
        }
        return address;
    }

    /// <summary>Скачивает файл, следуя не более чем пяти HTTPS-перенаправлениям без передачи авторизации.</summary>
    /// <param name="http">HTTP-клиент без заголовка авторизации бота.</param>
    /// <param name="url">Адрес загрузки.</param>
    /// <param name="attachment">Описание файла.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Временный файл с потоком в начале.</returns>
    /// <exception cref="AttachmentException">Файл превышает допустимый размер или ссылка недоступна.</exception>
    public static async Task<MediaFile> DownloadAsync(HttpClient http, string url, MessageAttachment attachment, CancellationToken ct)
    {
        if (attachment.Size > MaxBytes)
        {
            throw new AttachmentException("Вложение превышает предел передачи 50 МиБ.");
        }
        Uri address = Address(url);
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                address = Address(response.Headers.Location is { } location ? new Uri(address, location).AbsoluteUri : null);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException((int)response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new AttachmentException("Вложение превышает предел передачи 50 МиБ.");
            }
            string name = attachment.FileName ?? response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(address.LocalPath);
            name = Path.GetFileName(name.Replace('\\', '/'));
            name = string.Concat(name.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "attachment.bin";
            }
            string mime = attachment.ContentType ?? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            FileStream output = new(Path.Combine(Path.GetTempPath(), "bridge-media-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            try
            {
                using Stream input = await response.Content.ReadAsStreamAsync(ct);
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw new AttachmentException("Вложение превышает предел передачи 50 МиБ.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                output.Position = 0;
                return new MediaFile(output, name, mime);
            }
            catch
            {
                await output.DisposeAsync();
                throw;
            }
        }
        throw new AttachmentException("Превышено число перенаправлений при загрузке вложения.");
    }
}