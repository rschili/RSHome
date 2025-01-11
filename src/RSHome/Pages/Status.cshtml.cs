using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;

public class StatusModel : PageModel
{
    public IConfigService Config { get; init; }
    public DiscordWorkerService DiscordWorkerService { get; init; }
    public MatrixWorkerService MatrixWorkerService { get; init; }

    public bool MatrixRunning => MatrixWorkerService.IsRunning;

    public bool DiscordRunning => DiscordWorkerService.IsRunning;

    public StatusModel(IConfigService config, DiscordWorkerService discordWorkerService, MatrixWorkerService matrixWorkerService)
    {
        Config = config;
        DiscordWorkerService = discordWorkerService;
        MatrixWorkerService = matrixWorkerService;
    }
}
