using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography.X509Certificates;
using RSHome.Services;
using Microsoft.AspNetCore.Builder;
using System.Text;

Console.WriteLine("Loading variables...");
var config = ConfigService.LoadFromEnvFile();

var builder = WebApplication.CreateBuilder(args);
builder.Logging
    .ClearProviders()
    .AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = false;
        options.TimestampFormat = "hh:mm:ss ";
        options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
    })
    .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning) // Filter logs from HttpClient
    .SetMinimumLevel(LogLevel.Warning)
    .AddSeq(config.SeqUrl, config.SeqApiKey);

var sqliteService = await SqliteService.CreateAsync(config).ConfigureAwait(false);
builder.Services
    .AddSingleton<IConfigService>(config)
    .AddHttpClient()
    .AddSingleton(sqliteService)
    .AddSingleton<OpenAIService>()
    .AddSingleton<DiscordWorkerService>()
    .AddHostedService(p => p.GetRequiredService<DiscordWorkerService>())
    .AddSingleton<MatrixWorkerService>()
    .AddHostedService(p => p.GetRequiredService<MatrixWorkerService>())
    .AddRazorPages(options => {
        options.RootDirectory = "/Pages";
        options.Conventions.AuthorizeFolder("/", "admin");
        options.Conventions.AllowAnonymousToPage("/Index");
        options.Conventions.AllowAnonymousToPage("/Login");
    });
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
    .SetApplicationName("RSHome");

builder.Services.AddHttpClient(); // for matrix

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

var logger = app.Services.GetRequiredService<ILogger<Program>>();
app
    .Use(async (context, next) =>
    {
        await next.Invoke();
        if(context.Response.StatusCode >= 400)
            logger.LogWarning("{Method} for {Path} from {IP} resulted in HTTP {StatusCode}",
                context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, context.Response.StatusCode);
    });



app.MapStaticAssets();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
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

app.MapGet("/list-routes", (IEnumerable<EndpointDataSource> endpointSources, EndpointDataSource etc) =>
{
    var sb = new StringBuilder();
    foreach (var source in endpointSources)
    {
        foreach (var endpoint in source.Endpoints)
        {
            sb.AppendLine($"Type: {endpoint.ToString()} Label: { endpoint.DisplayName}");
            foreach(var metadata in endpoint.Metadata)
            {
                sb.AppendLine($"       Metadata: {metadata}");
            }
        }
    }

    return sb.ToString();
}).RequireAuthorization("admin");

app.Run();
