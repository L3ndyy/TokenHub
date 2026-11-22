using System.Text.Json;

namespace TokenHub.Services;

/// <summary>
/// Pulls token usage out of an answer that is never a JSON document in memory.
/// OpenCode always asks for a stream, and Token Harbor reports the totals in a final
/// server-sent-event chunk (<c>choices: []</c> plus <c>usage</c>), so the JSON-only
/// counter in the proxy never saw a single token and the dashboard showed 0 in / 0 out.
/// </summary>
public sealed class UsageScanner
{
    private readonly Action<long, long> _report;
    private string _pending = string.Empty;

    public UsageScanner(Action<long, long> report) => _report = report;

    /// <summary>
    /// Scans one streamed chunk. Chunks may be split anywhere, even in the middle of a
    /// line or an event, so the tail is kept until its newline arrives.
    /// </summary>
    public void Scan(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        string text = _pending.Length == 0 ? chunk : _pending + chunk;
        int start = 0;
        int newline;
        while ((newline = text.IndexOf('\n', start)) >= 0)
        {
            ReadLine(text.AsSpan(start, newline - start));
            start = newline + 1;
        }

        _pending = text[start..];

        // A line without an end is not ours; drop it instead of buffering forever.
        if (_pending.Length > 64 * 1024)
        {
            _pending = string.Empty;
        }
    }

    /// <summary>Reports the usage of a complete JSON answer, if it has one.</summary>
    public void ScanJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            long prompt = Read(usage, "prompt_tokens") ?? Read(usage, "input_tokens") ?? 0;
            long completion = Read(usage, "completion_tokens") ?? Read(usage, "output_tokens") ?? 0;
            if (prompt != 0 || completion != 0)
            {
                _report(prompt, completion);
            }
        }
        catch
        {
            // Not a usage-bearing document; nothing to count.
        }
    }

    private void ReadLine(ReadOnlySpan<char> line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line = line[..^1];
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        var payload = line[5..].Trim();
        if (payload.Length < 2 || !payload.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return;
        }

        ScanJson(payload.ToString());
    }

    private static long? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : null;
}