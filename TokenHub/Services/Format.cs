using System.Globalization;

namespace TokenHub.Services;

/// <summary>Small formatting helpers so numbers read like Russian UI text, not raw digits.</summary>
public static class Format
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"1 234 ₽", "<1 ₽", "12,3 тыс. ₽".</summary>
    public static string Rub(double value)
    {
        if (value <= 0)
        {
            return "0 ₽";
        }

        if (value < 1)
        {
            return "<1 ₽";
        }

        if (value < 1000)
        {
            return value.ToString("N0", Ru) + " ₽";
        }

        if (value < 1_000_000)
        {
            return (value / 1000.0).ToString("N1", Ru) + " тыс. ₽";
        }

        return (value / 1_000_000.0).ToString("N1", Ru) + " млн ₽";
    }

    /// <summary>Compact token/request counts: "987", "12 тыс.", "4,71 млн".</summary>
    public static string Compact(long value)
    {
        if (value < 0)
        {
            value = 0;
        }

        if (value < 1000)
        {
            return value.ToString("N0", Ru);
        }

        if (value < 1_000_000)
        {
            return (value / 1000.0).ToString("N0", Ru) + " тыс.";
        }

        if (value < 1_000_000_000)
        {
            return (value / 1_000_000.0).ToString("N2", Ru) + " млн";
        }

        return (value / 1_000_000_000.0).ToString("N2", Ru) + " млрд";
    }

    /// <summary>Russian plural for a small counter: 1 запрос, 2 запроса, 5 запросов.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        int mod100 = count % 100;
        int mod10 = count % 10;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}