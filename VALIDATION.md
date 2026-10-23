# Проверка проекта

## Уточнение передачи опросов

- 45 проверок проходят: 37 прежних и 8 случаев для опросов, включая параметризованные сценарии ссылок.
- `TelegramPollCopyIncludesSnapshotAndOriginalLink` проверяет весь путь от Telegram-события до текста с вопросом, вариантами, счётчиками и ссылкой на оригинал.
- `PollLinksRespectChatType` проверяет приватные супергруппы и отсутствие вымышленных ссылок обычных групп.
- `ClosedMultipleChoicePollKeepsVoterTotal` проверяет режим, закрытие и отличие числа участников от суммы голосов.
- `QuizCopyDoesNotRevealCorrectAnswer` исключает раскрытие правильного варианта и объяснения викторины.
- `PartialPollDoesNotInventResultsOrMode` проверяет отсутствие вымышленных нулевых результатов и настроек.
- `MaxPollClearlyReportsUnsupportedVoting` проверяет резервное сообщение для недокументированного вложения poll, сохраняя текст источника.
- Интерактивное голосование и синхронизация результатов не реализованы; тесты не создают опросы в реальных группах.

## Расширение передачи медиа (Windows)

- .NET SDK 10.0.402; сборка Release с `GenerateDocumentationFile=true` и `-warnaserror` проходит без ошибок и предупреждений.
- При внедрении медиа прошли 37 проверок: 11 прежних и 26 новых сценариев медиа, включая параметризованные случаи.
- Передача Telegram → MAX: `TelegramPhotoUploadsToMaxAndRetriesUsingSameToken` и `MaxAudioVideoAndFileUploadsUseDocumentedTokens` проверяют байты multipart, токены и повтор после `attachment.not.ready`.
- Передача MAX → Telegram: `MaxFilesUploadToTelegramWithCaption` проверяет фото, видео, аудио и документы вместе с подписью.
- Разбор медиа и ссылок: `TelegramPhotoAndCaptionLinksArePreserved`, `TelegramMediaTypesAreRecognized`, `LivePhotoKeepsPhotoAndVideoOnce`, `MaxAttachmentsLinksAndSpecialContentArePreserved`.
- Миграция очереди и перезапуск: `MediaQueueMigratesOldDatabaseAndPersistsPreparedToken` сохраняет прежние сообщения, новые вложения, длинные подписи и токен загрузки.
- Обновление ссылок и временные файлы: `MaxDownloadRefreshesMatchingAttachmentAndDeletesTemporaryFile`.
- Ограничения размера и адресов: `OversizedAndUnsafeAttachmentsAreRejectedWithoutNetwork`.
- Проверки используют подставной HTTP-транспорт; сообщения в настоящие группы из тестов не отправляются. После перезапуска приложения нужна ручная проверка новыми вложениями в обе стороны.

## Первоначальная проверка текстового моста

- Сборка и тесты выполнены на .NET SDK 10.0.100 в Linux.
- 11 автоматических тестов прошли, ошибок и предупреждений в итоговом прогоне нет.
- Публикация `win-x64`, framework-dependent: успешно. В выходной папке есть
  `MessengerBridge.exe` и нативная SQLite для Windows x64.
- `--status` с пустой БД отрабатывает без токенов.
- `--check` без токенов завершает работу с понятной ошибкой конфигурации.
- Настоящие токены и соединения с пользовательскими ботами не использовались.
- Запуск опубликованного EXE на Windows и скриптов в Windows PowerShell 5.1
  в этой среде не выполнялся. Следующая проверка на целевой машине —
  `Start-Bridge.ps1 -Check`, затем два новых сообщения в разных группах.

Тесты охватывают фильтрацию групп и ботов, Int64 ID, текст с emoji, дедупликацию,
перезапуск с SQLite, откат очереди вместе с курсором, отложенную отправку,
повторную постановку failed, HTTP Retry-After, миграцию Telegram-группы и формат
исходящих запросов обеих платформ. HTTP-тесты используют подставной transport.

При проверке решения в среде с запретом вспомогательных сокетов MSBuild можно
использовать последовательную сборку:

```powershell
dotnet test .\tests\MessengerBridge.Tests -c Release -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```