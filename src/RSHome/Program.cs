using System.Globalization;
using System.Security.Cryptography.X509Certificates;

Console.WriteLine($"Current user: {Environment.UserName}");
Console.WriteLine("Loading variables...");
var config = ConfigService.LoadFromEnvFile();

var cultureInfo = new CultureInfo("de-DE");
CultureInfo.DefaultThreadCurrentCulture   = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
Console.WriteLine($"Current culture: {CultureInfo.CurrentCulture.Name}");

var builder = WebApplication.CreateBuilder(args);
foreach (var s in builder.Configuration.Sources)
{   // Disable file watchers to avoid running out of inotify watches on Linux.
    // https://stackoverflow.com/questions/56360697/how-can-i-disable-the-defaul-aspnet-core-config-change-watcher
    if (s is FileConfigurationSource)
        ((FileConfigurationSource)s).ReloadOnChange = false;
}

builder.Logging
    .ClearProviders()
    .AddSimpleConsole(options =>
    {
        options.IncludeScopes   = true;
        options.SingleLine      = false;
        options.TimestampFormat = "hh:mm:ss ";
        options.ColorBehavior   = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
    })
    .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning)
    .SetMinimumLevel(LogLevel.Warning)
    .AddSeq(config.SeqUrl, config.SeqApiKey);

// --- Services ---
builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);

builder.Services
    .AddSingleton<IConfigService>(config)
    .AddSingleton<AuthService>()
    .AddSingleton<LoginGuard>()
    .AddSingleton<WordleService>()
    .AddHttpClient();

// Template engine (RazorEngineCore — compile-once, cache forever)
var templatesPath = Path.Combine(AppContext.BaseDirectory, "Templates");
builder.Services.AddSingleton(_ => new TemplateService(templatesPath));

// Kestrel: HTTPS with client cert
var x509 = X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, config.PfxPassword);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5001, listenOptions =>
    {
        listenOptions.UseHttps(x509);
    });
});

var app = builder.Build();

// --- Middleware pipeline ---
app.UseResponseCompression();
app.UseStaticFiles();

// Resolve authentication: read "auth" cookie → validate → set ctx.Items["Authenticated"].
app.Use(async (ctx, next) =>
{
    var token = AuthService.ReadCookie(ctx.Request);
    if (token != null)
    {
        var auth = ctx.RequestServices.GetRequiredService<AuthService>();
        if (await auth.ValidateAsync(token))
            ctx.Items["Authenticated"] = true;
    }
    await next();
});

// Log 4xx/5xx responses.
var logger = app.Services.GetRequiredService<ILogger<Program>>();
app.Use(async (context, next) =>
{
    await next.Invoke();
    if (context.Response.StatusCode >= 400)
        logger.LogInformation("{Method} for {Path} from {IP} resulted in HTTP {StatusCode}",
            context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, context.Response.StatusCode);
});

// --- Routes ---
app.MapAuth();
app.MapApp();

app.Run();
