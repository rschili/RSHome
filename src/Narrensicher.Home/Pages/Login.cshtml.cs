using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Narrensicher.Home;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.Tasks;

public class LoginModel : PageModel
{
    [BindProperty]
    public string? Username { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    public string? Password { get; set; }

    [BindProperty]
    public bool AcceptTerms { get; set; } = true;

    [FromServices]
    public Config Config { get; set; } = null!;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && BCrypt.Net.BCrypt.EnhancedVerify(Password!, Config.WebLoginHash))
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, Username!),
                new Claim(ClaimTypes.Role, "admin")
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity));

            return RedirectToPage("/Index");
        }

        // Optionally add an error message here
        return Page();
    }
}
