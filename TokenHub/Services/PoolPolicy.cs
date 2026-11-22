using System.Globalization;

namespace TokenHub.Services;

/// <summary>Why a key could not serve a request (item: per-model 402 is not an exhausted key).</summary>
internal enum KeyFault
{
    None,
    Invalid,
    Exhausted,
    ModelBlocked,
}

/// <summary>
/// Pure failover logic, separated from the HTTP proxy so it can be tested and reasoned
/// about on its own (items T5, T6, T7).
/// </summary>
internal static class PoolPolicy
{
    /// <summary>
    /// Decides whether another key can help. A region block cannot be fixed by
    /// switching, and "the plan does not cover this model" is not an exhausted key —
    /// it just means that model is unavailable on that account.
    /// </summary>
    public static KeyFault Classify(int status, string body)
    {
        if (body.Contains("region_blocked", StringComparison.OrdinalIgnoreCase)
            || body.Contains("sanction", StringComparison.OrdinalIgnoreCase))
        {
            return KeyFault.None;
        }

        if (status == 401
            || body.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
            || body.Contains("revoked", StringComparison.OrdinalIgnoreCase))
        {
            return KeyFault.Invalid;
        }

        bool balanceZero = body.Contains("balance_zero", StringComparison.OrdinalIgnoreCase);
        bool modelScoped = body.Contains("cover this model", StringComparison.OrdinalIgnoreCase)
                           || body.Contains("not available for this model", StringComparison.OrdinalIgnoreCase)
                           || body.Contains("model is not available", StringComparison.OrdinalIgnoreCase);

        if (status == 402 && (modelScoped || (balanceZero && body.Contains("model", StringComparison.OrdinalIgnoreCase))))
        {
            return KeyFault.ModelBlocked;
        }

        if (status is 402 or 429)
        {
            return KeyFault.Exhausted;
        }

        if (status == 403)
        {
            bool keyish = body.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("quota", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("credit", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("balance", StringComparison.OrdinalIgnoreCase);
            return keyish ? KeyFault.Exhausted : KeyFault.None;
        }

        return KeyFault.None;
    }

    /// <summary>
    /// Cooldown for an exhausted key. Honours a Retry-After header (item T5) and falls
    /// back to the configured number of minutes.
    /// </summary>
    public static TimeSpan Cooldown(string? retryAfter, int minutes)
    {
        int fallback = Math.Clamp(minutes, 1, 24 * 60);

        if (!string.IsNullOrWhiteSpace(retryAfter))
        {
            string value = retryAfter.Trim();

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 24 * 3600));
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment))
            {
                var span = moment - DateTimeOffset.UtcNow;
                if (span > TimeSpan.Zero)
                {
                    return span > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : span;
                }
            }
        }

        return TimeSpan.FromMinutes(fallback);
    }

    /// <summary>
    /// Orders the usable keys by the configured strategy (item T6). The first element is
    /// the one the proxy should try next.
    /// </summary>
    public static List<ApiKeyEntry> Rank(
        IReadOnlyList<ApiKeyEntry> available,
        string? activeId,
        string strategy,
        Func<string, int> usage,
        int cursor)
    {
        if (available.Count == 0)
        {
            return new List<ApiKeyEntry>();
        }

        switch (strategy)
        {
            case "roundRobin":
                int start = ((cursor % available.Count) + available.Count) % available.Count;
                return available.Skip(start).Concat(available.Take(start)).ToList();

            case "leastUsed":
                return available
                    .OrderBy(k => usage(k.Id))
                    .ThenBy(k => k.Id == activeId ? 0 : 1)
                    .ToList();

            default:
                // Keep using the key that worked last time, then fall through in list order.
                return available
                    .OrderBy(k => k.Id == activeId ? 0 : 1)
                    .ToList();
        }
    }

    /// <summary>
    /// Order used when nothing is ready to serve: probe the key that recovers soonest
    /// rather than giving up for the whole cooldown. Keys flagged invalid come last —
    /// a single transient 401 must not keep a working key switched off forever.
    /// </summary>
    public static List<ApiKeyEntry> ProbeOrder(IReadOnlyList<ApiKeyEntry> pool) =>
        pool.OrderBy(k => k.Invalid ? 1 : 0)
            .ThenBy(k => k.ExhaustedUntilUtc ?? DateTime.MinValue)
            .ToList();

    /// <summary>
    /// Carries the health the pool learned (cooldowns, invalid flags, blocked models) onto
    /// a freshly loaded settings list, so an unrelated save by the desktop app cannot
    /// un-pause a key mid-cooldown. Keys the pool has never seen keep the loaded values.
    /// </summary>
    public static void PreserveHealth(IReadOnlyList<ApiKeyEntry> fresh, IReadOnlyList<ApiKeyEntry> known)
    {
        foreach (var key in fresh)
        {
            var old = known.FirstOrDefault(k => k.Id == key.Id);
            if (old is null)
            {
                continue;
            }

            key.Invalid = old.Invalid;
            key.ExhaustedUntilUtc = old.ExhaustedUntilUtc;
            key.BlockedModels = old.BlockedModels;
            key.LastStatus = old.LastStatus;
        }
    }

    /// <summary>Default sort for the on-screen key list (item V6); never reorders the saved pool.</summary>
    public static List<ApiKeyEntry> SortForDisplay(IReadOnlyList<ApiKeyEntry> keys, string sort)
    {
        var list = keys.ToList();
        switch (sort)
        {
            case "status":
                return list.OrderBy(Rank0).ToList();
            case "name":
                return list.OrderBy(k => k.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            default:
                return list;
        }
    }

    private static int Rank0(ApiKeyEntry key) =>
        key.Invalid ? 0
        : key.ExhaustedUntilUtc > DateTime.UtcNow ? 1
        : !key.Enabled ? 3
        : key.Available ? 2
        : 4;
}
