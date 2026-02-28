using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Security.Cryptography;

namespace RSHome.Services;

/// <summary>
/// Session-based auth for a single-user site. No claims, no principals, no framework magic.
///
/// Flow:
///   1. Login: BCrypt.EnhancedVerify password against WEB_LOGIN_HASH → store token in memory
///   2. Middleware: read "auth" cookie → ValidateAsync → ctx.Items["Authenticated"] = true
///   3. Logout: remove token from dictionary → clear cookie
///
/// Sessions are in-memory and reset on app restart. This is intentional for a single-user
/// personal site where security risk from restart is negligible.
/// </summary>
public sealed class AuthService(IConfigService config)
{
    private const int SessionDays = 14;
    private const string CookieName = "auth";

    // token → expiry
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    /// <summary>
    /// Validates credentials. Returns the new session token on success, null on failure.
    /// Username is cosmetic — only the password is checked.
    /// </summary>
    public Task<string?> LoginAsync(string password)
    {
        bool verified;
        try
        {
            string calculatedHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password);
            Console.WriteLine($"Calculated hash: {calculatedHash}");
            string hash = "$2a$11$F2PD8Xq6HXTSReRDqtZfK.8M2TWXQ.O160sfXpgZRb4ue1/kFFau2";
            bool matches = config.WebLoginHash == hash;
            verified = BCrypt.Net.BCrypt.EnhancedVerify(password, config.WebLoginHash);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error verifying password: {e}");
            return Task.FromResult<string?>(null);
        }

        if (!verified)
            return Task.FromResult<string?>(null);

        var token  = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiry = DateTimeOffset.UtcNow.AddDays(SessionDays);
        _sessions[token] = expiry;

        return Task.FromResult<string?>(token);
    }

    /// <summary>
    /// Returns true if the token is known and not expired.
    /// </summary>
    public Task<bool> ValidateAsync(string token)
    {
        if (_sessions.TryGetValue(token, out var expiry) && expiry > DateTimeOffset.UtcNow)
            return Task.FromResult(true);

        // Clean up expired token lazily.
        _sessions.TryRemove(token, out _);
        return Task.FromResult(false);
    }

    /// <summary>Removes the session. Call on logout.</summary>
    public Task RevokeAsync(string token)
    {
        _sessions.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    // --- Cookie helpers ---

    public static void SetCookie(HttpResponse response, string token)
    {
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly  = true,
            Secure    = true,
            SameSite  = SameSiteMode.Lax,
            MaxAge    = TimeSpan.FromDays(SessionDays),
        });
    }

    public static void ClearCookie(HttpResponse response)
    {
        response.Cookies.Delete(CookieName);
    }

    public static string? ReadCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var v) ? v : null;
}
