namespace TokenHub.Services;

/// <summary>
/// A rough "what would this have cost on a paid model" estimate for the savings counter.
/// It is deliberately simple and offline: no price API, no network call, no billing.
/// Numbers are typical public list prices in USD per 1M tokens, converted at a fixed rate.
/// </summary>
public static class Pricing
{
    private const double RubPerUsd = 100.0;

    // (substring of the model id, USD per 1M input, USD per 1M output). First match wins,
    // so put the more specific ids before the family fallback.
    private static readonly (string Match, double In, double Out)[] Table =
    {
        ("gpt-4o-mini", 0.15, 0.60),
        ("gpt-4o", 2.50, 10.00),
        ("gpt-4.1", 2.00, 8.00),
        ("gpt-4", 10.00, 30.00),
        ("o4-mini", 1.10, 4.40),
        ("o3", 2.00, 8.00),
        ("o1", 15.00, 60.00),
        ("claude-4", 3.00, 15.00),
        ("claude-3-7", 3.00, 15.00),
        ("claude-3-5", 3.00, 15.00),
        ("claude", 3.00, 15.00),
        ("gemini-2.5-pro", 1.25, 10.00),
        ("gemini-2.5-flash", 0.30, 2.50),
        ("gemini", 0.30, 2.50),
        ("deepseek-reasoner", 0.55, 2.19),
        ("deepseek", 0.27, 1.10),
        ("qwen", 0.30, 1.20),
        ("kimi", 0.60, 2.50),
        ("glm", 0.60, 2.20),
        ("llama", 0.20, 0.60),
        ("mistral", 0.20, 0.60),
        ("mimo", 0.30, 1.00),
    };

    private const double DefaultIn = 0.50;
    private const double DefaultOut = 1.50;

    /// <summary>Estimated rouble value of the given tokens on the model's paid equivalent.</summary>
    public static double Rub(long promptTokens, long completionTokens, string? model)
    {
        var (inUsd, outUsd) = Lookup(model);
        double usd = promptTokens / 1_000_000.0 * inUsd
                     + completionTokens / 1_000_000.0 * outUsd;
        return usd * RubPerUsd;
    }

    private static (double In, double Out) Lookup(string? model)
    {
        if (!string.IsNullOrEmpty(model))
        {
            string id = model.ToLowerInvariant();
            foreach (var row in Table)
            {
                if (id.Contains(row.Match, StringComparison.Ordinal))
                {
                    return (row.In, row.Out);
                }
            }
        }

        return (DefaultIn, DefaultOut);
    }
}