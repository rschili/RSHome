namespace RSHome.Services;

/// <summary>
/// In-memory login protection: escalating delays per account, temporary lockout,
/// and cross-account IP rate limiting to catch credential stuffing.
///
/// State is intentionally in-memory and resets on app restart.
/// All public methods are thread-safe.
/// </summary>
public sealed class LoginGuard
{
    // Delay applied before the credential check, indexed by failure count.
    private static readonly TimeSpan[] AccountDelays =
    [
        TimeSpan.Zero,             // 0
        TimeSpan.Zero,             // 1
        TimeSpan.Zero,             // 2
        TimeSpan.Zero,             // 3
        TimeSpan.FromSeconds(5),   // 4
        TimeSpan.FromSeconds(15),  // 5
        TimeSpan.FromSeconds(60),  // 6+
    ];

    private const int AccountLockThreshold = 10;
    private const int AccountLockMinutes   = 30;
    private const int IpLockThreshold      = 20;
    private const int IpLockMinutes        = 15;

    private sealed record AccountEntry(int Failures, DateTimeOffset? LockedUntil);
    private sealed record IpEntry(int Failures, DateTimeOffset LastFailure);

    private readonly Dictionary<string, AccountEntry> _accounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IpEntry> _ips =
        new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>
    /// Returns the delay to apply before attempting login and whether the attempt is outright blocked.
    /// Does NOT mutate state — call <see cref="RecordFailure"/> or <see cref="RecordSuccess"/> after.
    /// </summary>
    public (TimeSpan Delay, bool Blocked) Check(string ip)
    {
        lock (_sync)
        {
            // --- IP check ---
            if (_ips.TryGetValue(ip, out var ipEntry) && ipEntry.Failures >= IpLockThreshold)
            {
                if (DateTimeOffset.UtcNow - ipEntry.LastFailure < TimeSpan.FromMinutes(IpLockMinutes))
                    return (TimeSpan.FromSeconds(60), Blocked: true);
                _ips.Remove(ip);
            }

            // --- Account check (single account key for RSHome) ---
            if (_accounts.TryGetValue("admin", out var acct))
            {
                if (acct.LockedUntil.HasValue)
                {
                    if (acct.LockedUntil > DateTimeOffset.UtcNow)
                        return (TimeSpan.FromSeconds(60), Blocked: true);
                    _accounts.Remove("admin");
                    return (TimeSpan.Zero, Blocked: false);
                }

                var delay = acct.Failures < AccountDelays.Length
                    ? AccountDelays[acct.Failures]
                    : AccountDelays[^1];
                return (delay, Blocked: false);
            }

            return (TimeSpan.Zero, Blocked: false);
        }
    }

    /// <summary>Increments failure counters for the account and IP.</summary>
    public void RecordFailure(string ip)
    {
        lock (_sync)
        {
            var prev        = _accounts.GetValueOrDefault("admin");
            var newFailures = (prev?.Failures ?? 0) + 1;

            var lockedUntil = prev?.LockedUntil
                ?? (newFailures >= AccountLockThreshold
                    ? DateTimeOffset.UtcNow.AddMinutes(AccountLockMinutes)
                    : (DateTimeOffset?)null);

            _accounts["admin"] = new AccountEntry(newFailures, lockedUntil);

            var prevIp = _ips.GetValueOrDefault(ip);
            _ips[ip] = new IpEntry((prevIp?.Failures ?? 0) + 1, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Clears failure state on successful login.</summary>
    public void RecordSuccess()
    {
        lock (_sync)
        {
            _accounts.Remove("admin");
        }
    }
}
