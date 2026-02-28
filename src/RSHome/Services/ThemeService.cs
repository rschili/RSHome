namespace RSHome.Services;

/// <summary>
/// Stateless theme helper. Theme preference is stored in a browser cookie ("theme").
/// Three states: "auto" (follow OS), "dark", "light".
/// No database — single-user site, cookie-only persistence.
/// </summary>
public static class ThemeService
{
    public static readonly string[] ValidThemes = ["auto", "dark", "light"];

    public static string GetFromCookie(HttpRequest request) =>
        request.Cookies.TryGetValue("theme", out var v) && ValidThemes.Contains(v) ? v : "auto";

    public static string Cycle(string current) => current switch
    {
        "auto"  => "dark",
        "dark"  => "light",
        "light" => "auto",
        _       => "dark",
    };

    public static void SetCookie(HttpResponse response, string theme)
    {
        response.Cookies.Append("theme", theme, new CookieOptions
        {
            MaxAge      = TimeSpan.FromDays(365),
            SameSite    = SameSiteMode.Strict,
            HttpOnly    = false, // must be readable by the pre-paint inline script
            IsEssential = true,
        });
    }

    /// <summary>
    /// The HTML for the theme toggle button. Shows the NEXT state so the user
    /// knows what clicking will do.
    /// </summary>
    public static string ButtonHtml(string theme) => theme switch
    {
        "dark"  => """<button class="btn-theme" hx-post="/theme" hx-target="this" hx-swap="outerHTML" aria-label="Switch to: Light">&#9728;</button>""",
        "light" => """<button class="btn-theme" hx-post="/theme" hx-target="this" hx-swap="outerHTML" aria-label="Switch to: Auto">&#9680;</button>""",
        _       => """<button class="btn-theme" hx-post="/theme" hx-target="this" hx-swap="outerHTML" aria-label="Switch to: Dark">&#9790;</button>""",
    };
}
