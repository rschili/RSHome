using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;

public class StatusModel : PageModel
{
    public Config Config { get; init; }
    public DiscordWorkerService DiscordWorkerService { get; init; }
    public MatrixWorkerService MatrixWorkerService { get; init; }

    public StatusModel(Config config, DiscordWorkerService discordWorkerService, MatrixWorkerService matrixWorkerService)
    {
        Config = config;
        DiscordWorkerService = discordWorkerService;
        MatrixWorkerService = matrixWorkerService;
    }
}
