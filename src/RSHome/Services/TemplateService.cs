using RazorEngineCore;
using System.Collections.Concurrent;

namespace RSHome.Services;

/// <summary>
/// Compiles every *.cshtml template once at start-up and keeps the result in a
/// thread-safe cache. Subsequent renders just execute the already-compiled assembly —
/// no Roslyn invocation per request.
///
/// Handles the two-pass layout pattern:
///  1. Run the child template → captures rendered body + Layout name + ViewBag.
///  2. If Layout is set, run the layout template with RenderBodyCallback injected.
/// </summary>
public sealed class TemplateService
{
    private readonly ConcurrentDictionary<string, IRazorEngineCompiledTemplate<RSHomeTemplateBase>> _cache = new();

    /// <summary>
    /// Pre-compiles all *.cshtml files in <paramref name="templatesDirectory"/>.
    /// Call once at startup — compilation is expensive; this is intentionally eager.
    /// </summary>
    public TemplateService(string templatesDirectory)
    {
        var engine = new RazorEngine();

        // References needed by the compiled templates.
        var rsHomeAssembly = typeof(RSHomeTemplateBase).Assembly;

        foreach (var file in Directory.EnumerateFiles(templatesDirectory, "*.cshtml"))
        {
            var name    = Path.GetFileNameWithoutExtension(file);
            var content = File.ReadAllText(file);

            try
            {
                var compiled = engine.Compile<RSHomeTemplateBase>(content, builder =>
                {
                    builder.AddAssemblyReference(rsHomeAssembly);
                    // Ensure Roslyn can resolve all loaded BCL assemblies.
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location))
                            builder.AddAssemblyReference(asm);
                    }

                    // Default namespaces — mirrors what ASP.NET Core Razor injects globally.
                    builder.AddUsing("System");
                    builder.AddUsing("System.Collections.Generic");
                    builder.AddUsing("System.Linq");
                    builder.AddUsing("System.Threading.Tasks");
                    builder.AddUsing("System.Globalization");
                    builder.AddUsing("RSHome.Services");
                    builder.AddUsing("RSHome.Endpoints");
                });

                _cache[name] = compiled;
            }
            catch (Exception ex)
            {
                // Fail fast — a broken template is a deployment error, not a runtime one.
                throw new InvalidOperationException(
                    $"Failed to compile template '{name}' from {file}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Renders <paramref name="templateName"/> with <paramref name="model"/>,
    /// applying the layout declared inside the template (if any).
    /// Thread-safe: compiled templates are stateless; new instance created per call.
    /// </summary>
    public async Task<string> RenderAsync(string templateName, object? model = null)
    {
        var compiled = GetTemplate(templateName);

        // Step 1 — render the child template.
        RSHomeTemplateBase? instance = null;
        string body = await compiled.RunAsync(t =>
        {
            t.Model  = model;
            instance = t;
        });

        if (instance?.Layout is not string layoutName)
            return body;

        // Step 2 — render the layout, injecting the child body + shared ViewBag.
        var layoutCompiled = GetTemplate(layoutName);
        var sharedViewBag  = instance.ViewBag;

        return await layoutCompiled.RunAsync(t =>
        {
            t.ViewBag            = sharedViewBag;
            t.RenderBodyCallback = () => body;
        });
    }

    private IRazorEngineCompiledTemplate<RSHomeTemplateBase> GetTemplate(string name)
    {
        if (_cache.TryGetValue(name, out var compiled))
            return compiled;

        throw new InvalidOperationException(
            $"Template '{name}' is not cached. Available: {string.Join(", ", _cache.Keys)}");
    }
}
