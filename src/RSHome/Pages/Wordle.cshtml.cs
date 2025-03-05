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

    public List<string> Results { get; } = new();

    public async Task OnGetAsync()
    {

        
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ModelState.AddModelError(string.Empty, "Sorry, this is still WIP, please come back later.");

        return Page();
    }

    private async Task ConstructResults()
    {
        var boardStateCookie = Request.Cookies["BoardState"];
        if (!string.IsNullOrEmpty(boardStateCookie))
        {
            Results.Add(boardStateCookie);
        }
    }




}
