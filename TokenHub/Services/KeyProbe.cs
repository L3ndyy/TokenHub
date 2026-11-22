using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>Checks a Token Harbor key against the real API (through the Vercel proxy).</summary>
public static class KeyProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<(bool Ok, string Status)> CheckAsync(ApiKeyEntry key)
    {
        var settings = App.Settings;
        string url = $"{settings.DirectBaseUrl}/models";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);

            using var response = await Http.SendAsync(request);
            int code = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync();

            key.LastCheckUtc = DateTime.UtcNow;

            if (code == 200)
            {
                // /models only proves the key is accepted. It says nothing about the free
                // chat quota, so an existing pause must survive — otherwise the startup
                // sweep un-pauses a key the pool just learned was exhausted.
                key.Invalid = false;

                if (key.ExhaustedUntilUtc is null || key.ExhaustedUntilUtc <= DateTime.UtcNow)
                {
                    key.ExhaustedUntilUtc = null;
                    key.LastStatus = $"ключ принят · {Count(body)} моделей";
                }

                return (true, key.LastStatus);
            }

            if (code == 401)
            {
                key.Invalid = true;
                key.LastStatus = "недействителен";
                return (false, key.LastStatus);
            }

            if (code is 402 or 429)
            {
                key.ExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, settings.CooldownMinutes));
                key.LastStatus = $"исчерпан (HTTP {code})";
                return (false, key.LastStatus);
            }

            key.LastStatus = $"HTTP {code}";
            return (false, key.LastStatus);
        }
        catch (Exception ex)
        {
            key.LastStatus = "ошибка сети";
            return (false, ex.Message);
        }
        finally
        {
            SettingsStore.Save(App.Settings);
        }
    }

    private static int Count(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}