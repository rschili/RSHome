using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;
using RSHome.Models;
using System.Collections.Immutable;

public class StatusModel : PageModel
{
    public IConfigService Config { get; init; }
    public DiscordWorkerService DiscordWorkerService { get; init; }
    public MatrixWorkerService MatrixWorkerService { get; init; }

    public string? Message { get; set; }

    public ImmutableArray<JoinedTextChannel<ulong>> TextChannels { get; set;} = new();

    public ImmutableArray<ChannelUser<ulong>>? SelectedChannelUsers { get; set; } = null;
    public ulong? SelectedChannelId { get; set; } = null;

    public bool MatrixRunning => MatrixWorkerService.IsRunning;

    public bool DiscordRunning => DiscordWorkerService.IsRunning;

    public StatusModel(IConfigService config, DiscordWorkerService discordWorkerService, MatrixWorkerService matrixWorkerService)
    {
        Config = config;
        DiscordWorkerService = discordWorkerService;
        MatrixWorkerService = matrixWorkerService;
        TextChannels = DiscordWorkerService.TextChannels;
    }


    public void OnGet()
    {
    }

    public Task<IActionResult> OnPostAsync()
    {
        if (bool.TryParse(Request.Form["startDialogue"], out bool startDialogueValue) && startDialogueValue)
            return Task.FromResult(PostStartDialogue());

        return Task.FromResult<IActionResult>(Page());
    }

    private IActionResult PostStartDialogue()
    {
        var channelField = Request.Form["channel"];
        if (string.IsNullOrWhiteSpace(channelField) || !ulong.TryParse(channelField, out var channelId))
        {
            ModelState.AddModelError("channel", "Channel is required.");
            return Page();
        }

        var validatedChannel = TextChannels.FirstOrDefault(c => c.Id == channelId);
        if (validatedChannel == null)
        {
            ModelState.AddModelError("channel", "Channel is invalid.");
            return Page();
        }

        SelectedChannelUsers = validatedChannel.Users;
        SelectedChannelId = validatedChannel.Id;

        var userField = Request.Form["userId"];
        if (string.IsNullOrWhiteSpace(userField) || !ulong.TryParse(userField, out var userId))
        {
            Message = "Bitte ein Ziel wählen.";
            return Page();
        }
        var messagesField = Request.Form["messages"];
        if (string.IsNullOrWhiteSpace(messagesField) || !int.TryParse(messagesField, out var messagesCount))
        {
            ModelState.AddModelError("messages", "Nachrichtenanzahl ist erforderlich.");
            return Page();
        }

        _ = Task.Run(() => DiscordWorkerService.StartDialogueAsync(channelId, userId, messagesCount).ConfigureAwait(false))
            .ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    // Log the exception (assuming you have a logger, replace 'Logger' with your actual logger instance)
                    DiscordWorkerService.Logger.LogError(task.Exception, "An error occurred while starting the dialogue.");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);

        return Page();
    }
}
