using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RSHome.Services;
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
    public IConfigService Config { get; set; } = null!;

    [FromServices]
    public SecurityService Security { get; set; } = null!;

    public async Task<IActionResult> OnPostAsync()
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        bool rateLimitExceeded = false;
        if (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && Security.Authenticate(Password, ipAddress, out rateLimitExceeded))
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

        if(rateLimitExceeded)
            ModelState.AddModelError(string.Empty, "Rate limit exceeded. Stop.");
        /*else
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");*/

        return Page();
    }
}
