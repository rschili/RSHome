using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

public class WordleModel : PageModel
{
    [BindProperty]
    public string? Input { get; set; }

    [FromServices]
    public WordleService Service { get; set; } = null!;

    public List<WordleGridCell> Results { get; } = new();

    public IActionResult OnGet()
    {
        string[]? tippHistory = GetTippHistory();
        if(tippHistory != null)
        {
            var results = Service.CheckTipps(tippHistory);
            RenderResults(results, tippHistory);
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        var input = Input?.Trim()?.ToUpper();
        if(input == null || !WordleService.IsValidInput(input))
        {
            ModelState.AddModelError(nameof(Input), "Please enter a 5 letter word using only alphanumeric chars.");
            return Page();
        }

        string[]? tippHistory = GetTippHistory();
        string[] inputs = tippHistory != null ? [.. tippHistory, input] : new[] { input };
        var results = Service.CheckTipps(inputs);
        RenderResults(results, inputs);

        var currentDate = DateTime.Now;
        var boardStateCookie = $"{currentDate:yyyy-MM-dd}:{string.Join(":", inputs)}";
        Response.Cookies.Append("BoardState", boardStateCookie, 
            new CookieOptions { Expires = currentDate.AddDays(1), HttpOnly = true, IsEssential = true }); 

        return Page();
    }

    private const string GreenColor = "#417e3b";
    private const string YellowColor = "#917d27";

    private void RenderResults(List<string> results, string[] inputs)
    {
        if(results.Count != inputs.Length)
            return; // Should not happen, ever

        for(int index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var input = inputs[index];

            if(result == WordleService.InvalidIndicator)
            {
                ModelState.AddModelError(nameof(Input), $"Invalid input: {input}");
                return;
            }
            if(result == WordleService.CorrectIndicator)
            {
                for(int i = 0; i < input.Length; i++)
                {
                    Results.Add(new WordleGridCell(input[i], GreenColor));
                }
                return;
            }

            if(result.Length != input.Length || result.Length != 5)
                return; // Should not happen, ever

            for(int i = 0; i < input.Length; i++)
            {
                var letter = input[i];
                var color = result[i] switch
                {
                    WordleService.LetterCorrect => GreenColor,
                    WordleService.LetterMisplaced => YellowColor,
                    _ => null
                };
                Results.Add(new WordleGridCell(letter, color));
            }
        }
    }

    private string[]? GetTippHistory()
    {
        var boardStateCookie = Request.Cookies["BoardState"];
        if (string.IsNullOrEmpty(boardStateCookie))
            return null;
            
        var parts = boardStateCookie.Split(':');
        if (parts.Length != 2)
            return null;

        if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
        {
            return null;
        }

        var today = DateTime.Now;
        if (parsedDate.DayOfYear != today.DayOfYear)
        {
            // The cookie is from a different day
            return null;
        }

        var inputs = parts[1..];
        if(inputs.Any(i => !WordleService.IsValidInput(i)))
        { // There is some crap in the inputs
            return null;
        }

        // Ensure everything is uppercase
        inputs = inputs.Select(i => i.ToUpper()).ToArray();
        return inputs;
    }
}

public record WordleGridCell(char Letter, string? Color);
