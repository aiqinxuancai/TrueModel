using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrueModel;

namespace TrueModel.Tests;

public class NotificationTests
{
    [Fact]
    public async Task SendsBothChannelsWithoutLeakingCredentialsAndRecordsBusinessFailure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var provider = new EphemeralDataProtectionProvider(); var protector = provider.CreateProtector("Notifications.v1");
        var settings = new NotificationSettings { WebhookEnabled = true, WebhookProtected = protector.Protect("https://hook.test/secret-route"), PushDeerEnabled = true, PushKeyProtected = protector.Protect("push-secret") };
        var handler = new CaptureHandler();
        var service = new NotificationService(new HttpClient(handler), provider);
        var payload = new NotificationPayload("detection.completed", 5, "Completed", 100, 1, 1, [new("site", "key label", "mock", "Success", 100, "candidate", .95)]);
        await service.Deliver(db, settings, payload, CancellationToken.None);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.DoesNotContain("push-secret", handler.Bodies[0]);
        using var webhook = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(5, webhook.RootElement.GetProperty("runId").GetInt32());
        Assert.Contains("pushkey=push-secret", handler.Bodies[1]);
        var deliveries = await db.NotificationDeliveries.OrderBy(d => d.Id).ToArrayAsync();
        Assert.True(deliveries[0].Success);
        Assert.False(deliveries[1].Success);
        Assert.DoesNotContain("push-secret", JsonSerializer.Serialize(deliveries));
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = 100, error = "denied" }) };
        }
    }
}
