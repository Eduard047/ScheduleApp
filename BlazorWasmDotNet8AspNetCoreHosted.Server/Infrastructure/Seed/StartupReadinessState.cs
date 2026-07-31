namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;

public sealed class StartupReadinessState
{
    private volatile bool _isReady;
    private string _statusMessage = "Триває початкове налаштування довідників.";

    public bool IsReady => _isReady;
    public string StatusMessage => Volatile.Read(ref _statusMessage);

    public void MarkReady()
    {
        Volatile.Write(ref _statusMessage, "Початкове налаштування завершено.");
        _isReady = true;
    }

    public void MarkRetrying()
    {
        _isReady = false;
        Volatile.Write(ref _statusMessage, "Початкове налаштування очікує доступу до бази даних.");
    }
}

public sealed class DefaultLessonTypesSeederHostedService(
    IServiceProvider services,
    StartupReadinessState readiness,
    ILogger<DefaultLessonTypesSeederHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DefaultLessonTypesSeeder.SeedAsync(services, stoppingToken);
                readiness.MarkReady();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                readiness.MarkRetrying();
                logger.LogWarning(
                    exception,
                    "Не вдалося виконати початкове наповнення типів занять. Повторна спроба буде через {RetrySeconds} секунд.",
                    RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
