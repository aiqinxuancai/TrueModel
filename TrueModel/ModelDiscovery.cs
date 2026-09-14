using System.Net.Http.Headers;
using System.Text.Json;

namespace TrueModel;

public sealed class ModelDiscovery(HttpClient client)
{
    public static string ModelsUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.EndsWith("/models", StringComparison.Ordinal)) return url;
        const string suffix = "/chat/completions";
        return ModelTraceClient.CompletionUrl(url, false)[..^suffix.Length] + "/models";
    }

    public async Task<string[]> Fetch(string baseUrl, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(baseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new ModelDiscoveryException($"获取模型失败（HTTP {(int)response.StatusCode}），请检查站点地址及 Key 权限");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new ModelDiscoveryException("模型接口返回格式不正确，需要 data 模型数组");
            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString()!.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { throw new ModelDiscoveryException("模型接口返回的内容不是有效 JSON"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ModelDiscoveryException("获取模型超时，请重试或手动添加"); }
        catch (HttpRequestException) { throw new ModelDiscoveryException("无法连接模型接口，请检查站点地址或稍后重试"); }
    }
}

public sealed class ModelDiscoveryException(string message) : Exception(message);
