using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography.X509Certificates;
using Narrensicher.Home;

Console.WriteLine("Loading variables...");
Config config = Config.LoadFromEnvFile();

var builder = WebApplication.CreateBuilder(args);
builder.Logging
    .AddSimpleConsole()
    .SetMinimumLevel(LogLevel.Warning);
builder.Services
    .AddSingleton(config)
    .AddRazorPages();
builder.Services
    .AddAntiforgery()
    //.AddHealthChecks();
    //AddProblemDetails();
    .AddAuthentication()
        .AddCookie(options =>
        {
            options.LoginPath = "/Login";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
        });

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(config.WebKeyStore))
    .SetApplicationName("Narrensicher.Home");

builder.Services.AddAuthorizationBuilder().AddPolicy("admin", policy => policy.RequireRole("admin"));

var x509 = X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, config.PfxPassword);
builder.WebHost.ConfigureKestrel(options =>
{
    // options.ListenAnyIP(5000); // HTTP port
    options.ListenAnyIP(5001, listenOptions =>
    {
        listenOptions.UseHttps(x509);
    });
});

using var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
app.Use(async (context, next) =>
{
    await next.Invoke();
    if(context.Response.StatusCode >= 400)
        logger.LogWarning("{Method} for {Path} from {IP} resulted in HTTP {StatusCode}",
            context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, context.Response.StatusCode);
});

app.MapStaticAssets();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorPages();

app.MapGet("/ticket", async context =>
{
    var ticket = await context.AuthenticateAsync();
    if (!ticket.Succeeded)
    {
        await context.Response.WriteAsync($"Signed Out");
        return;
    }

    foreach (var (key, value) in ticket.Properties.Items)
    {
        await context.Response.WriteAsync($"{key}: {value}\r\n");
    }
});

app.Run();
