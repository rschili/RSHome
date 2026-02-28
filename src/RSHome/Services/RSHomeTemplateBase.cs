using RazorEngineCore;
using System.Dynamic;
using System.Net;

namespace RSHome.Services;

/// <summary>
/// Base class for all RSHome Razor templates. Provides:
/// - HTML encoding by default (override Write so @expr is safe)
/// - Layout / RenderBody support (two-pass render handled by TemplateService)
/// - ViewBag (null-safe dynamic bag shared between child and layout)
/// </summary>
public abstract class RSHomeTemplateBase : RazorEngineTemplateBase
{
    // @{ Layout = "_layout"; } in child templates sets this.
    public string? Layout { get; set; }

    // Set by TemplateService before running the layout template.
    public Func<string>? RenderBodyCallback { get; set; }

    // Child template sets ViewBag.Title etc.; TemplateService passes the same instance to the layout.
    public dynamic ViewBag { get; set; } = new NullSafeDynamicBag();

    // @Model.X — all access is dynamic so no @model directive needed in templates.
    // 'new' because the non-generic base already has object? Model; we want dynamic.
    public new dynamic? Model { get; set; }

    // Renders the child body inside a layout. Returns RawHtmlString so Write() emits it unencoded.
    public RawHtmlString RenderBody() =>
        new(RenderBodyCallback?.Invoke()
            ?? throw new InvalidOperationException("RenderBody() called outside of a layout context."));

    // HTML-encode @expr output by default (same as ASP.NET Core Razor behaviour).
    // WriteLiteral() is NOT overridden — literal HTML in the template stays raw.
    public override void Write(object? obj)
    {
        if (obj is RawHtmlString raw)
            WriteLiteral(raw.Value);
        else if (obj is not null)
            WriteLiteral(WebUtility.HtmlEncode(obj.ToString()));
    }

    /// <summary>Wrap a value with Raw() to emit unencoded HTML: @Raw(someHtml)</summary>
    public static RawHtmlString Raw(object? value) => new(value?.ToString() ?? string.Empty);
}

/// <summary>
/// Opt-in typed base class. Replace the top of a template:
///   @inherits RSHome.Services.RSHomeTemplateBase&lt;MyModel&gt;
///
/// Model.SomeProp is now fully typed — typos become Roslyn compile errors at startup.
/// </summary>
public abstract class RSHomeTemplateBase<T> : RSHomeTemplateBase
{
    public new T Model
    {
        get => (T)base.Model!;
        set => base.Model = value;
    }
}

public readonly record struct RawHtmlString(string Value);

/// <summary>
/// DynamicObject-based ViewBag that silently returns null for missing properties,
/// matching ASP.NET Core ViewBag semantics (no RuntimeBinderException on unknown keys).
/// </summary>
public sealed class NullSafeDynamicBag : DynamicObject
{
    private readonly Dictionary<string, object?> _data = new(StringComparer.OrdinalIgnoreCase);

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        _data.TryGetValue(binder.Name, out result);
        return true; // always succeed — missing key → null
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _data[binder.Name] = value;
        return true;
    }
}
