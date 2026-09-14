using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed class NotificationSettings
{
    public int Id { get; set; } = 1;
    public bool WebhookEnabled { get; set; }
    public string WebhookProtected { get; set; } = "";
    public bool PushDeerEnabled { get; set; }
    public string PushDeerEndpoint { get; set; } = "https://api2.pushdeer.com/message/push";
    public string PushKeyProtected { get; set; } = "";
    public bool OnlyFailures { get; set; }
}
public sealed class NotificationDelivery
{
    public int Id { get; set; }
    public int? RunId { get; set; }
    public string Channel { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
public sealed record NotificationInput(bool WebhookEnabled, string? WebhookUrl, bool PushDeerEnabled, string PushDeerEndpoint, string? PushKey, bool OnlyFailures, bool ClearWebhook = false, bool ClearPushKey = false);
public sealed record ResultSummary(string Site, string KeyName, string Model, string Status, long DurationMs, string? Prediction, double? Probability);
public sealed record NotificationPayload(string Event, int? RunId, string Status, long DurationMs, int Total, int Succeeded, ResultSummary[] Results);

public sealed class NotificationService(HttpClient client, IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("Notifications.v1");
    public static bool ValidEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public async Task NotifyRun(AppDb db, DetectionRun run, CancellationToken token)
    {
        var settings = await db.Notifications.SingleAsync(token);
        if (!settings.WebhookEnabled && !settings.PushDeerEnabled) return;
        var results = await db.Results.AsNoTracking().Where(r => r.RunId == run.Id).ToArrayAsync(token);
        if (settings.OnlyFailures && run.Status == "Completed" && results.Length == run.Total && results.All(r => r.Status == "Success")) return;
        var summaries = results.Select(r =>
        {
            string? prediction = null; double? probability = null;
            if (r.AttributionJson is not null)
            {
                using var json = JsonDocument.Parse(r.AttributionJson);
                var winner = json.RootElement.GetProperty("Candidates")[0];
                prediction = winner.GetProperty("DisplayName").GetString(); probability = winner.GetProperty("Probability").GetDouble();
            }
            return new ResultSummary(r.SiteName, r.KeyName, r.ModelName, r.Status, r.LatencyMs, prediction, probability);
        }).ToArray();
        var payload = new NotificationPayload("detection.completed", run.Id, run.Status, (long)((run.CompletedAt ?? DateTime.UtcNow) - run.StartedAt).TotalMilliseconds, run.Total, results.Count(r => r.Status == "Success"), summaries);
        await Deliver(db, settings, payload, token);
    }
    public async Task Deliver(AppDb db, NotificationSettings settings, NotificationPayload payload, CancellationToken token)
    {
        foreach (var channel in new[] { "Webhook", "PushDeer" })
        {
            if (channel == "Webhook" ? !settings.WebhookEnabled : !settings.PushDeerEnabled) continue;
            var delivery = new NotificationDelivery { RunId = payload.RunId, Channel = channel };
            try
            {
                HttpRequestMessage request;
                if (channel == "Webhook")
                {
                    request = new(HttpMethod.Post, protector.Unprotect(settings.WebhookProtected)) { Content = JsonContent.Create(payload) };
                    request.Headers.TryAddWithoutValidation("X-TrueModel-Event", payload.Event);
                }
                else
                {
                    var summary = string.Join('\n', payload.Results.Select(r => $"{r.Site} / {r.KeyName} / {r.Model}: {r.Status}，{r.DurationMs / 1000d:F1}s，归因 {r.Prediction ?? "无"} {(r.Probability.HasValue ? r.Probability.Value.ToString("P2") : "")}"));
                    request = new(HttpMethod.Post, settings.PushDeerEndpoint)
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                            ["pushkey"] = protector.Unprotect(settings.PushKeyProtected), ["type"] = "text",
                            ["text"] = $"TrueModel 检测完成：成功 {payload.Succeeded}/{payload.Total}，耗时 {payload.DurationMs / 1000d:F1}s\n" + summary[..Math.Min(summary.Length, 8000)] })
                    };
                }
                using (request)
                using (var response = await client.SendAsync(request, token))
                {
                    if (!response.IsSuccessStatusCode) delivery.Message = $"HTTP {(int)response.StatusCode}";
                    else if (channel == "PushDeer")
                    {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                        delivery.Success = body.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0;
                        delivery.Message = delivery.Success ? "已提交 PushDeer" : "PushDeer 返回业务错误";
                    }
                    else { delivery.Success = true; delivery.Message = "Webhook 已接受"; }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.Security.Cryptography.CryptographicException or InvalidOperationException or UriFormatException)
            { delivery.Message = "通知发送失败：网络、超时或配置错误"; }
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
