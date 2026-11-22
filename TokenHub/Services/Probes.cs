using System.Net.Http;
using System.Net.Http.Headers;

namespace TokenHub.Services;

public sealed record ProbeResult(bool Ok, string Summary, string Detail);

/// <summary>Network checks: the proxy itself, the upstream, and the local key pool.</summary>
public static class Probes
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public static async Task<ProbeResult> ProxyHealthAsync(AppSettings settings)
    {
        var url = $"{settings.ProxyBase.TrimEnd('/')}/{settings.ProxySecret}/health";
        return await SendAsync(HttpMethod.Get, url, null, "Прокси");
    }

    public static async Task<ProbeResult> ProxyModelsAsync(AppSettings settings, string? apiKey)
    {
        var url = $"{settings.ProxyBase.TrimEnd('/')}/{settings.ProxySecret}/v1/models";
        return await SendAsync(HttpMethod.Get, url, apiKey, "Модели через прокси");
    }

    public static async Task<ProbeResult> DirectUpstreamAsync(AppSettings settings, string? apiKey)
    {
        var url = $"{settings.Upstream.TrimEnd('/')}/v1/models";
        return await SendAsync(HttpMethod.Get, url, apiKey, "Напрямую (без прокси)");
    }

    /// <summary>Models served by the local key-pool proxy; the proxy injects a working key.</summary>
    public static async Task<ProbeResult> PoolModelsAsync(AppSettings settings)
    {
        var url = $"http://127.0.0.1:{settings.LocalPort}/v1/models";
        return await SendAsync(HttpMethod.Get, url, null, "Модели через пул ключей");
    }

    private static async Task<ProbeResult> SendAsync(
        HttpMethod method,
        string url,
        string? apiKey,
        string label)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await Http.SendAsync(request);
            int code = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync();

            string detail = Trim(body);
            if (code == 401 || code == 403)
            {
                detail = RegionHint(body) ?? detail;
            }

            return new ProbeResult(IsHealthy(code), $"{label}: HTTP {code}", detail);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"{label}: ошибка сети", ex.Message);
        }
    }

    private static bool IsHealthy(int code)
    {
        // Token Harbor also answers 401 without an API key: the endpoint is reachable.
        return code is 200 or 401;
    }

    private static string? RegionHint(string body)
    {
        if (body.Contains("region_blocked", StringComparison.OrdinalIgnoreCase)
            || body.Contains("your region", StringComparison.OrdinalIgnoreCase)
            || body.Contains("region is not available", StringComparison.OrdinalIgnoreCase))
        {
            return "регион заблокирован (region_blocked)";
        }

        return null;
    }

    private static string Trim(string value)
    {
        var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 400 ? compact : compact[..400] + "…";
    }

}
