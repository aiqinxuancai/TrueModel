using System.Text.Json;

namespace TrueModel;

public static class FailureReasons
{
    public static string Describe(string status, int statusCode, string? error, string responsesJson)
    {
        var reasons = new List<string>();
        // Challenge errors contain upstream details that the attribution summary can omit.
        try
        {
            using var responses = JsonDocument.Parse(responsesJson);
            if (responses.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var response in responses.RootElement.EnumerateArray())
                    if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("Error", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                        reasons.Add(value.GetString()!);
        }
        catch (JsonException) { }
        if (!string.IsNullOrWhiteSpace(error)) reasons.Add(error switch
        {
            "Timeout" => "请求超时", "Cancelled" => "检测已取消", "Interrupted" => "检测因服务中断而停止", _ => error
        });
        if (reasons.Count == 0) reasons.Add(status switch
        {
            "Timeout" => "请求超时", "Cancelled" => "检测已取消", "Interrupted" => "检测因服务中断而停止",
            _ => statusCode >= 400 ? $"接口返回 HTTP {statusCode}" : "未记录具体失败原因"
        });
        return string.Join('\n', reasons.Distinct(StringComparer.Ordinal));
    }
}
