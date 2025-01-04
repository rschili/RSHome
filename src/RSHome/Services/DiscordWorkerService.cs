using Microsoft.Extensions.Hosting;

namespace RSHome.Services;

public class DiscordWorkerService : BackgroundService
{
    private readonly ILogger _logger;
    private readonly Config _config;

    public DiscordWorkerService(ILogger<DiscordWorkerService> logger, Config config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Discord worker service");
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Consume Scoped Service Hosted Service is stopping.");

        await base.StopAsync(stoppingToken);
    }
}