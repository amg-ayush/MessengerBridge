using MessengerBridge.Clients;
using MessengerBridge.Configuration;
using MessengerBridge.Persistence;
using MessengerBridge.Workers;

namespace MessengerBridge;

/// <summary>
/// Настраивает и запускает мост либо выполняет выбранную команду обслуживания.
/// </summary>
public static class Program
{
    /// <summary>
    /// Выполняет запуск приложения, проверку подключений или обслуживание очереди.
    /// </summary>
    /// <param name="args">Аргументы командной строки: --check, --status, --retry-failed либо пустой список.</param>
    /// <returns>Код завершения: 0 при успехе, 1 при ошибке выполнения, 2 при неверных аргументах.</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--check" or "--status" or "--retry-failed")))
            {
                Console.Error.WriteLine("Usage: MessengerBridge [--check | --status | --retry-failed]");
                return 2;
            }
            string? mode = args.FirstOrDefault();
            // Config and data resolve beside the compiled app, independent of the current directory.
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory
            });
            // Load for local dotnet run even if DOTNET_ENVIRONMENT isn't Development.
            builder.Configuration.AddUserSecrets<BridgeOptions>(optional: true).AddEnvironmentVariables();
            BridgeOptions options = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
            options.Validate(requireTokens: mode is not ("--status" or "--retry-failed"));
            options.DatabasePath = Path.GetFullPath(options.DatabasePath, AppContext.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
            // Prevent two instances from delivering from the same local outbox.
            using FileStream instanceLock = new(options.DatabasePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);

            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(_ => new BridgeStore(options.DatabasePath));
            // No IHttpClientFactory logging: Telegram's token is part of its URL.
            builder.Services.AddSingleton(_ => new TelegramClient(ApiTransport.Create(options.Telegram,
                new Uri("https://api.telegram.org/")), options));
            builder.Services.AddSingleton(_ => new MaxClient(ApiTransport.Create(options.Max,
                new Uri("https://platform-api2.max.ru/")), options));
            builder.Services.AddHostedService<BridgeWorker>();
            using IHost host = builder.Build();

            if (mode == "--check")
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
                await host.Services.GetRequiredService<TelegramClient>().VerifyAsync(cts.Token);
                Console.WriteLine("Telegram: OK (bot, group, administrator, no Webhook).");
                await host.Services.GetRequiredService<MaxClient>().VerifyAsync(cts.Token);
                Console.WriteLine("MAX: OK (TLS, bot, group, administrator, no Webhook).");
                return 0;
            }
            BridgeStore store = host.Services.GetRequiredService<BridgeStore>();
            if (mode == "--retry-failed")
            {
                int count = await store.RetryFailedAsync(options.Telegram.ChatId, options.Max.ChatId);
                Console.WriteLine($"Requeued {count} failed message part(s) using current ChatId settings.");
                return 0;
            }
            if (mode == "--status")
            {
                IReadOnlyList<string> status = await store.GetStatusAsync();
                foreach (string line in status)
                {
                    Console.WriteLine(line);
                }

                if (status.Count == 0)
                {
                    Console.WriteLine("Queue is empty; no cursors saved yet.");
                }

                return 0;
            }
            Console.WriteLine("MessengerBridge: Telegram <-> MAX. Stop with Ctrl+C.");
            Console.WriteLine($"Database: {options.DatabasePath}");
            await host.RunAsync();
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            // Do not dump HTTP exception details: these can contain the Telegram token.
            Console.Error.WriteLine(ex is InvalidOperationException ? ex.Message : SafeError.Describe(ex));
            if (ex is IOException)
            {
                Console.Error.WriteLine("Check the data path and that another bridge instance isn't running.");
            }

            return 1;
        }
    }
}