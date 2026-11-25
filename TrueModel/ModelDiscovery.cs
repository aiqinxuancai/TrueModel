using System.Net.Http.Headers;
using System.Text.Json;

namespace TrueModel;

public sealed class ModelDiscovery(HttpClient client)
{
    private const int MaxPages = 10;

    public static string ModelsUrl(string baseUrl) => ModelsUrl(baseUrl, ApiProtocols.OpenAi);

    public static string ModelsUrl(string baseUrl, string protocol)
    {
        var url = ApiProtocols.StripQueryAndFragment(baseUrl).TrimEnd('/');
        if (protocol == ApiProtocols.Gemini)
        {
            if (url.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(":streamGenerateContent", StringComparison.OrdinalIgnoreCase))
            {
                var modelsIndex = url.LastIndexOf("/models/", StringComparison.OrdinalIgnoreCase);
                if (modelsIndex >= 0) return url[..(modelsIndex + "/models".Length)];
            }
            if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/chat/completions".Length];
            else if (url.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/responses".Length];
            else if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/messages".Length];
            if (url.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith("/v1alpha", StringComparison.OrdinalIgnoreCase))
                return url + "/models";
            return url + "/v1beta/models";
        }
        if (url.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return url;
        if (protocol == ApiProtocols.Anthropic)
        {
            const string suffix = "/messages";
            var completionPath = ApiProtocols.StripQueryAndFragment(ModelTraceClient.CompletionUrl(url, ApiProtocols.Anthropic));
            return completionPath[..^suffix.Length] + "/models";
        }
        const string openAiSuffix = "/chat/completions";
        return ModelTraceClient.ChatCompletionsUrl(url)[..^openAiSuffix.Length] + "/models";
    }

    public async Task<string[]> Fetch(string baseUrl, string key, CancellationToken token, string protocol = ApiProtocols.Auto)
    {
        var candidates = ApiProtocols.Candidates(baseUrl, protocol);
        ModelDiscoveryException? firstError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await FetchForProtocol(baseUrl, key, candidate, token);
            }
            catch (ModelDiscoveryException e) when (candidates.Length > 1 && !e.Transient)
            {
                firstError ??= e;
            }
        }
        throw firstError ?? new ModelDiscoveryException("获取模型失败，请检查站点地址、协议及 Key 权限");
    }

    private async Task<string[]> FetchForProtocol(string baseUrl, string key, string protocol, CancellationToken token)
    {
        var endpoint = ModelsUrl(baseUrl, protocol);
        var useBearerAuth = protocol == ApiProtocols.OpenAi ||
            (protocol == ApiProtocols.Anthropic && key.StartsWith("sk-ant-oat", StringComparison.OrdinalIgnoreCase));
        var authSwitched = false;
        var results = new List<string>();
        string? pageCursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var requestUrl = pageCursor is null
                ? endpoint
                : protocol == ApiProtocols.Gemini
                    ? $"{endpoint}?pageToken={Uri.EscapeDataString(pageCursor)}"
                    : $"{endpoint}?after_id={Uri.EscapeDataString(pageCursor)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ModelTraceClient.ApplyClientHeaders(request, protocol, key, useBearerAuth, sessionId: null, retries: 0, timeoutSeconds: 30, includeResponsesBeta: false);

            try
            {
                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 401 && protocol is ApiProtocols.Anthropic or ApiProtocols.Gemini && !authSwitched && page == 0)
                    {
                        authSwitched = true;
                        useBearerAuth = !useBearerAuth;
                        page--;
                        continue;
                    }
                    throw new ModelDiscoveryException($"获取模型失败（HTTP {(int)response.StatusCode}），请检查站点地址及 Key 权限");
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new ModelDiscoveryException("模型接口返回的内容不是有效 JSON");

                if (protocol == ApiProtocols.Gemini)
                {
                    if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                        throw new ModelDiscoveryException("模型接口返回格式不正确，需要 models 模型数组");
                    foreach (var item in models.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (item.TryGetProperty("supportedGenerationMethods", out var methods) &&
                            methods.ValueKind == JsonValueKind.Array && methods.GetArrayLength() > 0 &&
                            !methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String &&
                                string.Equals(m.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        string? raw = null;
                        if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) raw = name.GetString();
                        else if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) raw = id.GetString();
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var stripped = ApiProtocols.StripGeminiModelPrefix(raw);
                        if (stripped.Length > 0) results.Add(stripped);
                    }
                    pageCursor = root.TryGetProperty("nextPageToken", out var nextToken) && nextToken.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nextToken.GetString())
                        ? nextToken.GetString()
                        : null;
                    if (pageCursor is null) break;
                    continue;
                }

                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new ModelDiscoveryException("模型接口返回格式不正确，需要 data 模型数组");
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var trimmed = id.GetString()!.Trim();
                        if (trimmed.Length > 0) results.Add(trimmed);
                    }
                }
                if (protocol == ApiProtocols.Anthropic &&
                    root.TryGetProperty("has_more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True &&
                    root.TryGetProperty("last_id", out var lastId) && lastId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lastId.GetString()))
                {
                    pageCursor = lastId.GetString();
                    continue;
                }
                break;
            }
            catch (JsonException) { throw new ModelDiscoveryException("模型接口返回的内容不是有效 JSON"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ModelDiscoveryException("获取模型超时，请重试或手动添加", transient: true); }
            catch (HttpRequestException) { throw new ModelDiscoveryException("无法连接模型接口，请检查站点地址或稍后重试", transient: true); }
        }

        return results.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}

public sealed class ModelDiscoveryException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}
