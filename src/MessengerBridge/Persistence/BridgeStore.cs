using MessengerBridge.Services;
using Microsoft.Data.Sqlite;

namespace MessengerBridge.Persistence;

/// <summary>
/// Хранит курсоры чтения и очередь доставки в SQLite, последовательно выполняя операции с соединением.
/// </summary>
/// <remarks>Постановка сообщений и обновление курсора выполняются одной транзакцией. Дедупликация защищает очередь, но не исключает повтор после неопределённого результата сетевой отправки.</remarks>
public sealed class BridgeStore : IDisposable
{
    /// <summary>
    /// Единственное соединение с локальной SQLite-базой.
    /// </summary>
    private readonly SqliteConnection _connection;
    /// <summary>
    /// Синхронизирует доступ параллельных обработчиков к соединению.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>
    /// Открывает SQLite-базу, создаёт каталог и таблицы, включает WAL и полную синхронизацию записи.
    /// </summary>
    /// <param name="path">Путь к файлу базы данных.</param>
    public BridgeStore(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        _connection.Open();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS cursors (
                source TEXT PRIMARY KEY, value INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                source_chat_id INTEGER NOT NULL,
                source_message_id TEXT NOT NULL,
                part INTEGER NOT NULL,
                destination_chat_id INTEGER NOT NULL,
                text TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at INTEGER NOT NULL DEFAULT 0,
                target_message_id TEXT,
                last_error TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                sent_at TEXT,
                UNIQUE(source, source_chat_id, source_message_id, part)
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox(source, status, id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Выполняет синхронную операцию с базой под общей асинхронной блокировкой.
    /// </summary>
    /// <typeparam name="T">Тип результата операции.</typeparam>
    /// <param name="action">Операция, выполняемая после захвата блокировки.</param>
    /// <param name="ct">Токен отмены ожидания блокировки; не прерывает уже начатый SQL-запрос.</param>
    /// <returns>Результат переданной операции.</returns>
    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return action(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Читает сохранённый курсор заданной платформы.
    /// </summary>
    /// <param name="source">Платформа, для которой запрашивается курсор.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Сохранённый курсор либо null, если чтение ещё не фиксировалось.</returns>
    public Task<long?> GetCursorAsync(Platform source, CancellationToken ct = default) => LockedAsync(() =>
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursors WHERE source=$source";
        cmd.Parameters.AddWithValue("$source", source.ToString());
        return cmd.ExecuteScalar() is long value ? (long?)value : null;
    }, ct);

    // Enqueue and cursor advancement commit together, before acknowledging updates remotely.
    /// <summary>
    /// Атомарно ставит части сообщений в очередь и сохраняет курсор следующего чтения.
    /// </summary>
    /// <param name="source">Платформа, из которой получено сообщение.</param>
    /// <param name="destinationChatId">Идентификатор целевого группового чата.</param>
    /// <param name="batch">Порция сообщений и следующий курсор.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Число новых частей, добавленных в очередь без учёта дубликатов.</returns>
    /// <remarks>Повторы определяются по платформе, исходному чату, идентификатору сообщения и номеру части. При ошибке транзакция откатывает и очередь, и курсор.</remarks>
    public Task<int> SaveBatchAsync(Platform source, long destinationChatId, PollBatch batch,
        CancellationToken ct = default) => LockedAsync(() =>
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        int inserted = 0;
        foreach (IncomingMessage message in batch.Messages)
        {
            IReadOnlyList<string> parts = MessageFormatter.Format(source, message);
            for (int part = 0; part < parts.Count; part++)
            {
                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO outbox
                        (source,source_chat_id,source_message_id,part,destination_chat_id,text)
                    VALUES ($source,$chat,$message,$part,$destination,$text)
                    """;
                cmd.Parameters.AddWithValue("$source", source.ToString());
                cmd.Parameters.AddWithValue("$chat", message.SourceChatId);
                cmd.Parameters.AddWithValue("$message", message.SourceId);
                cmd.Parameters.AddWithValue("$part", part);
                cmd.Parameters.AddWithValue("$destination", destinationChatId);
                cmd.Parameters.AddWithValue("$text", parts[part]);
                inserted += cmd.ExecuteNonQuery();
            }
        }
        if (batch.NextCursor is { } cursor)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO cursors(source,value) VALUES($source,$value)
                ON CONFLICT(source) DO UPDATE SET value=excluded.value
                """;
            cmd.Parameters.AddWithValue("$source", source.ToString());
            cmd.Parameters.AddWithValue("$value", cursor);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        return inserted;
    }, ct);

    /// <summary>
    /// Возвращает самую раннюю ожидающую часть в одном направлении, не извлекая её из очереди.
    /// </summary>
    /// <param name="source">Платформа, из которой получено сообщение.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Первая ожидающая часть либо null, если очередь пуста.</returns>
    /// <remarks>Отложенная первая часть блокирует следующие; время NextAttemptAt проверяет отправитель.</remarks>
    public Task<OutboxItem?> GetNextAsync(Platform source, CancellationToken ct = default) => LockedAsync(() =>
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,destination_chat_id,text,attempts,next_attempt_at
            FROM outbox WHERE source=$source AND status='pending' ORDER BY id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$source", source.ToString());
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? new OutboxItem(reader.GetInt64(0), source, reader.GetInt64(1),
            reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4)) : null;
    }, ct);

    /// <summary>
    /// Отмечает часть доставленной и сохраняет идентификатор сообщения получателя.
    /// </summary>
    /// <param name="id">Идентификатор записи очереди.</param>
    /// <param name="targetId">Идентификатор отправленного сообщения в целевом мессенджере.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Число обновлённых записей.</returns>
    public Task<int> MarkSentAsync(long id, string targetId, CancellationToken ct = default) => LockedAsync(() =>
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE outbox SET status='sent',target_message_id=$target,
                sent_at=CURRENT_TIMESTAMP,last_error=NULL WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$target", targetId);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }, ct);

    /// <summary>
    /// Регистрирует ошибку отправки, увеличивает число попыток и назначает повтор либо статус failed.
    /// </summary>
    /// <param name="id">Идентификатор записи очереди.</param>
    /// <param name="safeError">Описание ошибки без секретов и текста переписки.</param>
    /// <param name="delay">Пауза до следующей разрешённой попытки.</param>
    /// <param name="permanent">true для статуса failed; false оставляет запись в pending.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Число обновлённых записей.</returns>
    public Task<int> MarkErrorAsync(long id, string safeError, TimeSpan delay, bool permanent,
        CancellationToken ct = default) => LockedAsync(() =>
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE outbox SET attempts=attempts+1,last_error=$error,status=$status,
                next_attempt_at=$next WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", safeError);
        cmd.Parameters.AddWithValue("$status", permanent ? "failed" : "pending");
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }, ct);

    /// <summary>
    /// Возвращает все failed-записи в очередь с текущими адресатами из конфигурации.
    /// </summary>
    /// <param name="telegramChatId">Текущий идентификатор группы Telegram.</param>
    /// <param name="maxChatId">Текущий идентификатор группы MAX.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Число повторно поставленных в очередь частей.</returns>
    /// <remarks>Время следующей попытки и последняя ошибка сбрасываются; накопленное число попыток сохраняется.</remarks>
    public Task<int> RetryFailedAsync(long telegramChatId, long maxChatId, CancellationToken ct = default) =>
        LockedAsync(() =>
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE outbox SET status='pending',next_attempt_at=0,last_error=NULL,
                    destination_chat_id=CASE WHEN source='Telegram' THEN $max ELSE $telegram END
                WHERE status='failed'
                """;
            cmd.Parameters.AddWithValue("$telegram", telegramChatId);
            cmd.Parameters.AddWithValue("$max", maxChatId);
            return cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>
    /// Получает счётчики состояний очереди и сохранённые курсоры без текста сообщений.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Строки состояния, пригодные для вывода в консоль.</returns>
    public Task<IReadOnlyList<string>> GetStatusAsync(CancellationToken ct = default) => LockedAsync(() =>
    {
        List<string> lines = [];
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source,status,COUNT(*) FROM outbox GROUP BY source,status ORDER BY source,status";
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                lines.Add($"{reader.GetString(0)} -> {(reader.GetString(0) == "Telegram" ? "MAX" : "Telegram")}: {reader.GetString(1)}={reader.GetInt64(2)}");
            }
        }

        cmd.CommandText = "SELECT source,value FROM cursors ORDER BY source";
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                lines.Add($"{reader.GetString(0)} cursor={reader.GetInt64(1)}");
            }
        }

        return (IReadOnlyList<string>)lines;
    }, ct);

    /// <summary>
    /// Освобождает соединение SQLite и семафор доступа к базе данных.
    /// </summary>
    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}