using System.Text;

namespace RSHome.Endpoints;

public record IndexViewModel(string Theme = "auto", bool Authenticated = false);

public record WordleViewModel(
    List<WordleGridCell> Results,
    GameState State,
    string Summary = "",
    string? Error = null,
    string Theme = "auto",
    bool Authenticated = false);

public static class AppEndpoints
{
    public static void MapApp(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (HttpContext ctx, TemplateService razor) =>
        {
            var theme = ThemeService.GetFromCookie(ctx.Request);
            var isAuth = ctx.Items["Authenticated"] is true;
            var html  = await razor.RenderAsync("index", new IndexViewModel(theme, isAuth));
            return Results.Content(html, "text/html");
        });

        app.MapGet("/wordle", async (HttpContext ctx, TemplateService razor, WordleService wordle) =>
        {
            var theme  = ThemeService.GetFromCookie(ctx.Request);
            var isAuth = ctx.Items["Authenticated"] is true;
            var model  = BuildWordleModel(ctx.Request, wordle, null, null, theme, isAuth);
            var html   = await razor.RenderAsync("wordle", model);
            return Results.Content(html, "text/html");
        });

        app.MapPost("/wordle", async (HttpContext ctx, TemplateService razor, WordleService wordle) =>
        {
            var input  = ((string?)ctx.Request.Form["Input"])?.Trim()?.ToUpper();
            var theme  = ThemeService.GetFromCookie(ctx.Request);
            var isAuth = ctx.Items["Authenticated"] is true;

            if (input == null || !WordleService.IsValidInput(input))
            {
                var model = BuildWordleModel(ctx.Request, wordle, null, "Please enter a 5 letter word using only alphabetic characters.", theme, isAuth);
                var errHtml = await razor.RenderAsync("wordle", model);
                return Results.Content(errHtml, "text/html");
            }

            string[]? tippHistory = GetTippHistory(ctx.Request);
            string[] inputs = tippHistory != null ? [.. tippHistory, input] : [input];
            var results = wordle.CheckTipps(inputs);
            var (cells, state, summary) = RenderResults(results, inputs);

            if (state != GameState.Invalid)
            {
                var today = DateTime.Now;
                var boardStateCookie = $"{today:yyyy-MM-dd}:{string.Join(":", inputs)}";
                ctx.Response.Cookies.Append("BoardState", boardStateCookie,
                    new CookieOptions { Expires = today.AddDays(1), HttpOnly = true, IsEssential = true });
            }

            var vm = new WordleViewModel(cells, state == GameState.Invalid ? GameState.InProgress : state, summary, state == GameState.Invalid ? $"Invalid word: {input}" : null, theme, isAuth);
            var html = await razor.RenderAsync("wordle", vm);
            return Results.Content(html, "text/html");
        });

        // Toggle the global theme preference: auto → dark → light → auto.
        // Returns the new button HTML so htmx can swap it in-place.
        app.MapPost("/theme", (HttpContext ctx) =>
        {
            var current = ThemeService.GetFromCookie(ctx.Request);
            var next    = ThemeService.Cycle(current);
            ThemeService.SetCookie(ctx.Response, next);

            ctx.Response.Headers["HX-Trigger"] = $"{{\"themeChanged\":\"{next}\"}}";
            return Results.Content(ThemeService.ButtonHtml(next), "text/html");
        });
    }

    private static WordleViewModel BuildWordleModel(HttpRequest request, WordleService wordle, string? inputError, string? generalError, string theme, bool authenticated = false)
    {
        string[]? tippHistory = GetTippHistory(request);
        if (tippHistory == null)
            return new WordleViewModel([], GameState.InProgress, "", generalError ?? inputError, theme, authenticated);

        var results = wordle.CheckTipps(tippHistory);
        var (cells, state, summary) = RenderResults(results, tippHistory);
        return new WordleViewModel(cells, state == GameState.Invalid ? GameState.InProgress : state, summary, generalError ?? inputError, theme, authenticated);
    }

    private static string[]? GetTippHistory(HttpRequest request)
    {
        var boardStateCookie = request.Cookies["BoardState"];
        if (string.IsNullOrEmpty(boardStateCookie))
            return null;

        var parts = boardStateCookie.Split(':');
        if (parts.Length < 2 || parts.Length > 7)
            return null;

        if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
            return null;

        if (parsedDate.DayOfYear != DateTime.Now.DayOfYear)
            return null;

        var inputs = parts[1..];
        if (inputs.Any(i => !WordleService.IsValidInput(i)))
            return null;

        return inputs.Select(i => i.ToUpper()).ToArray();
    }

    private const string GreenColor  = "#417e3b";
    private const string YellowColor = "#917d27";

    private static (List<WordleGridCell> Cells, GameState State, string Summary) RenderResults(
        List<string> results, string[] inputs)
    {
        var cells = new List<WordleGridCell>();

        for (int index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var input  = inputs[index];

            if (result == WordleService.InvalidIndicator)
                return (cells, GameState.Invalid, "");

            if (result == WordleService.CorrectIndicator)
            {
                foreach (var ch in input)
                    cells.Add(new WordleGridCell(ch, GreenColor));

                return (cells, GameState.Won, BuildSummary(results));
            }

            if (result.Length != input.Length || result.Length != 5)
                return (cells, GameState.InProgress, "");

            for (int i = 0; i < input.Length; i++)
            {
                var color = result[i] switch
                {
                    WordleService.LetterCorrect   => GreenColor,
                    WordleService.LetterMisplaced => YellowColor,
                    _                             => (string?)null
                };
                cells.Add(new WordleGridCell(input[i], color));
            }
        }

        if (results.Count >= 6)
            return (cells, GameState.Lost, BuildSummary(results));

        return (cells, GameState.InProgress, "");
    }

    private static string BuildSummary(List<string> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RS Wordle {DateTime.Now:yyyy-MM-dd} {results.Count}/6");

        foreach (var result in results)
        {
            if (result == WordleService.CorrectIndicator)
            {
                sb.AppendLine("✅✅✅✅✅");
                return sb.ToString();
            }
            for (int i = 0; i < result.Length; i++)
            {
                sb.Append(result[i] switch
                {
                    WordleService.LetterCorrect   => "✅",
                    WordleService.LetterMisplaced => "🟨",
                    _                             => "⬛"
                });
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
