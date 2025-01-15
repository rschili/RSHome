using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;

public class StatusModel : PageModel
{
    public IConfigService Config { get; init; }
    public DiscordWorkerService DiscordWorkerService { get; init; }
    //public MatrixWorkerService MatrixWorkerService { get; init; }

    public string? Message { get; set; }

    public bool MessageIsError { get; set; } = false;

    public List<JoinedTextChannel> TextChannels { get; set;} = new();

    public List<ChannelUser>? SelectedChannelUsers { get; set; } = null;
    public ulong? SelectedChannelId { get; set; } = null;

    //public bool MatrixRunning => MatrixWorkerService.IsRunning;

    public bool DiscordRunning => DiscordWorkerService.IsRunning;

    public StatusModel(IConfigService config, DiscordWorkerService discordWorkerService)//, MatrixWorkerService matrixWorkerService)
    {
        Config = config;
        DiscordWorkerService = discordWorkerService;
        //MatrixWorkerService = matrixWorkerService;
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
        var channel = Request.Form["channel"];
        if (string.IsNullOrWhiteSpace(channel) || !ulong.TryParse(channel, out var channelId))
        {
            Message = "Channel is required.";
            MessageIsError = true;
            return Page();
        }

        var validatedChannel = TextChannels.FirstOrDefault(c => c.Id == channelId);
        if (validatedChannel == null)
        {
            Message = "Channel is invalid.";
            MessageIsError = true;
            return Page();
        }

        SelectedChannelUsers = validatedChannel.Users;
        SelectedChannelId = validatedChannel.Id;

        var selectedUser = Request.Form["userId"];
        if (string.IsNullOrWhiteSpace(selectedUser) || !ulong.TryParse(selectedUser, out var userId))
        {
            return Page();
        }
/*        var userId = Request.Form["userid"];
        var messages = Request.Form["messages"];
        var channelId = Request.Form["channelid"];

        if (string.IsNullOrWhiteSpace(name))
            ModelState.AddModelError("name", "Name is required.");

        if (!ulong.TryParse(userId, out ulong userIdValue))
            ModelState.AddModelError("userid", "User ID is required and must be a valid unsigned integer.");

        if (!int.TryParse(messages, out int messagesCount))
            ModelState.AddModelError("messages", "Number of Messages must be a valid number.");

        if (!ulong.TryParse(channelId, out ulong channelIdValue))
            ModelState.AddModelError("channelid", "Channel ID is required and must be a valid unsigned integer.");

        if (!ModelState.IsValid)
            return Page();

        _ = Task.Run(() => DiscordWorkerService.StartDialogueAsync(name!, userIdValue, channelIdValue, messagesCount).ConfigureAwait(false))
            .ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    // Log the exception (assuming you have a logger, replace 'Logger' with your actual logger instance)
                    DiscordWorkerService.Logger.LogError(task.Exception, "An error occurred while starting the dialogue.");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);*/

        return Page();
    }
}
