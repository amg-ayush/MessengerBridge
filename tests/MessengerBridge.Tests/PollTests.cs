using System.Text.Json;
using MessengerBridge.Clients;
using MessengerBridge.Services;
using Xunit;

namespace MessengerBridge.Tests;

/// <summary>Проверяет корректность текстовых копий опросов и явное обозначение ограничений голосования.</summary>
public sealed class PollTests
{
    /// <summary>Проверяет пересылку опроса с вопросом, вариантами, данными API и ссылкой публичной группы.</summary>
    [Fact]
    public void TelegramPollCopyIncludesSnapshotAndOriginalLink()
    {
        JsonElement updates = Json("""
            [{"update_id":1,"message":{"message_id":7,"chat":{"id":-1001234567890,"type":"supergroup","username":"example_group"},
              "from":{"is_bot":false,"first_name":"Анна"},"poll":{
                "id":"poll-id","question":"Когда встречаемся?","type":"regular","is_anonymous":true,"is_closed":false,
                "allows_multiple_answers":false,"total_voter_count":3,
                "options":[{"text":"Сегодня","voter_count":2},{"text":"Завтра","voter_count":1}]}}}]
            """);
        PollBatch batch = TelegramClient.ParseUpdates(updates, -1001234567890, null);
        IncomingMessage message = Assert.Single(batch.Messages);
        Assert.Equal(2, batch.NextCursor);
        Assert.Contains("Опрос: Когда встречаемся?", message.Text);
        Assert.Contains("Текстовая копия. Голосование в этой копии недоступно; откройте исходный чат Telegram.", message.Text);
        Assert.Contains("Режим: анонимный; один вариант ответа.", message.Text);
        Assert.Contains("Статус на момент пересылки: открыт.", message.Text);
        Assert.Contains("1. Сегодня — голосов: 2", message.Text);
        Assert.Contains("2. Завтра — голосов: 1", message.Text);
        Assert.Contains("Проголосовало участников: 3.", message.Text);
        Assert.Contains("Голоса, результаты и закрытие опроса после пересылки не синхронизируются.", message.Text);
        Assert.Contains("https://t.me/example_group/7", message.Text);
        Assert.Empty(message.Attachments!);
    }

    /// <summary>Проверяет ссылки приватных супергрупп и отсутствие вымышленных ссылок обычных групп.</summary>
    /// <param name="type">Тип исходного чата.</param>
    /// <param name="id">Идентификатор чата.</param>
    /// <param name="hasLink">Должна ли присутствовать ссылка на сообщение.</param>
    [Theory]
    [InlineData("supergroup", -1001234567890, true)]
    [InlineData("group", -5369085414, false)]
    [InlineData("group", -1001234567890, false)]
    public void PollLinksRespectChatType(string type, long id, bool hasLink)
    {
        JsonElement message = Json(JsonSerializer.Serialize(new
        {
            message_id = 123,
            chat = new { id, type },
            poll = new { question = "Вопрос", options = new[] { new { text = "Да" } } }
        }));
        string text = PollTextFormatter.FormatTelegram(message);
        if (hasLink)
        {
            Assert.Contains("Оригинал: https://t.me/c/1234567890/123", text);
        }
        else
        {
            Assert.DoesNotContain("https://t.me/", text);
            Assert.DoesNotContain("tg://", text);
        }
    }

    /// <summary>Проверяет закрытый опрос с несколькими вариантами без суммирования голосов вместо числа участников.</summary>
    [Fact]
    public void ClosedMultipleChoicePollKeepsVoterTotal()
    {
        string text = PollTextFormatter.FormatTelegram(Json("""
            {"poll":{"question":"Что берём?","is_anonymous":false,"is_closed":true,"allows_multiple_answers":true,
              "total_voter_count":2,"options":[{"text":"Чай","voter_count":2},{"text":"Кофе","voter_count":2}]}}
            """));
        Assert.Contains("Режим: неанонимный; можно выбрать несколько вариантов.", text);
        Assert.Contains("Статус на момент пересылки: завершён.", text);
        Assert.Contains("Проголосовало участников: 2.", text);
        Assert.DoesNotContain("Проголосовало участников: 4.", text);
    }

    /// <summary>Проверяет, что текстовая копия викторины не раскрывает правильный вариант и объяснение.</summary>
    [Fact]
    public void QuizCopyDoesNotRevealCorrectAnswer()
    {
        string text = PollTextFormatter.FormatTelegram(Json("""
            {"poll":{"question":"Сколько будет два плюс два?","type":"quiz","correct_option_ids":[1],
              "explanation":"секретное объяснение","options":[{"text":"Три"},{"text":"Четыре"}]}}
            """));
        Assert.Contains("Викторина: Сколько будет два плюс два?", text);
        Assert.Contains("1. Три", text);
        Assert.Contains("2. Четыре", text);
        Assert.DoesNotContain("секретное объяснение", text);
        Assert.DoesNotContain("правильный", text);
    }

    /// <summary>Проверяет отсутствие вымышленных нулевых результатов и настроек в неполных событиях.</summary>
    [Fact]
    public void PartialPollDoesNotInventResultsOrMode()
    {
        string text = PollTextFormatter.FormatTelegram(Json("""{"poll":{"question":"Вопрос","options":[{"text":"Да"}]}}"""));
        Assert.Contains("1. Да", text);
        Assert.DoesNotContain("голосов:", text);
        Assert.DoesNotContain("Проголосовало участников:", text);
        Assert.DoesNotContain("Режим:", text);
        Assert.DoesNotContain("Статус на момент пересылки:", text);
    }

    /// <summary>Проверяет понятное пояснение при встрече недокументированного типа опроса MAX без догадок о его схеме.</summary>
    [Fact]
    public void MaxPollClearlyReportsUnsupportedVoting()
    {
        string text = MessageContent.MaxText(Json("""
            {"text":"Исходный текст","attachments":[{"type":"poll","payload":{}}]}
            """));
        Assert.StartsWith("Исходный текст", text);
        Assert.Contains("Опрос MAX:", text);
        Assert.Contains("Откройте исходный чат MAX для голосования.", text);
        Assert.DoesNotContain("[Вложение MAX: poll]", text);
    }

    /// <summary>Разбирает тестовый JSON с независимым временем жизни.</summary>
    /// <param name="json">Строка тестового сценария.</param>
    /// <returns>Копия корневого JSON-элемента.</returns>
    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}