namespace MessengerBridge.Clients;

/// <summary>
/// Загруженный временный файл; закрытие потока автоматически удаляет его с диска.
/// </summary>
/// <param name="content">Открытый поток файла с позицией в начале.</param>
/// <param name="fileName">Безопасное имя для multipart-загрузки.</param>
/// <param name="contentType">MIME-тип файла.</param>
public sealed class MediaFile(Stream content, string fileName, string contentType) : IDisposable
{
    /// <summary>Поток содержимого, принадлежащий этому объекту.</summary>
    public Stream Content { get; } = content;

    /// <summary>Имя файла без каталогов и управляющих символов.</summary>
    public string FileName { get; } = fileName;

    /// <summary>MIME-тип для загрузки на другую платформу.</summary>
    public string ContentType { get; } = contentType;

    /// <summary>Закрывает поток и удаляет временный файл.</summary>
    public void Dispose() => Content.Dispose();
}