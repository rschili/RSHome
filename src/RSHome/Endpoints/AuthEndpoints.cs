namespace RSHome.Endpoints;

public record LoginPageModel(string? Error = null);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", async (HttpContext ctx, TemplateService razor) =>
        {
            if (ctx.Items["Authenticated"] is true)
                return Results.Redirect("/");

            var theme = ThemeService.GetFromCookie(ctx.Request);
            var html  = await razor.RenderAsync("login", new LoginPageModel());
            return Results.Content(html, "text/html");
        });

        app.MapPost("/login", async (HttpContext ctx, TemplateService razor, AuthService auth, LoginGuard guard) =>
        {
            var form     = ctx.Request.Form;
            var password = (string?)form["password"] ?? "";
            var ip       = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (string.IsNullOrWhiteSpace(password))
            {
                var html = await razor.RenderAsync("login", new LoginPageModel("Please enter your password."));
                return Results.Content(html, "text/html");
            }

            var (delay, blocked) = guard.Check(ip);

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            string? token = null;
            if (!blocked)
                token = await auth.LoginAsync(password);

            if (token is not null)
            {
                guard.RecordSuccess();
                AuthService.SetCookie(ctx.Response, token);
                return Results.Redirect("/");
            }

            guard.RecordFailure(ip);

            var failHtml = await razor.RenderAsync("login", new LoginPageModel("Invalid password."));
            return Results.Content(failHtml, "text/html");
        });

        app.MapPost("/logout", async (HttpContext ctx, AuthService auth) =>
        {
            var token = AuthService.ReadCookie(ctx.Request);
            if (token != null)
                await auth.RevokeAsync(token);

            AuthService.ClearCookie(ctx.Response);
            return Results.Redirect("/login");
        });
    }
}
