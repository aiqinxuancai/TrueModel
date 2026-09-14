using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class FailureReasonTests
{
    [Fact]
    public void IncludesConcreteChallengeErrorsAndDeduplicatesRepeatedFailures()
    {
        var reason = FailureReasons.Describe("Failed", 401, "有效回答不足", """[{"Error":"HTTP 401: invalid key"},{"Error":"HTTP 401: invalid key"},{"Error":"HTTP 429: quota exceeded"}]""");
        Assert.Equal("HTTP 401: invalid key\nHTTP 429: quota exceeded\n有效回答不足", reason);
    }

    [Theory]
    [InlineData("Timeout", 0, "Timeout", "请求超时")]
    [InlineData("Cancelled", 0, null, "检测已取消")]
    [InlineData("Failed", 403, null, "接口返回 HTTP 403")]
    [InlineData("Failed", 0, null, "未记录具体失败原因")]
    public void HandlesOldOrIncompleteRecords(string status, int code, string? error, string expected) =>
        Assert.Equal(expected, FailureReasons.Describe(status, code, error, "invalid json"));

    [Fact]
    public async Task TaskFailuresAreNotLimitedToRecentHistoryPage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-failures-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
            ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
        })));
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var run = new DetectionRun { Status = "Completed", Total = 206, Completed = 206, CompletedAt = DateTime.UtcNow,
                TargetsJson = JsonSerializer.Serialize(new[] { new Target(123, "snapshot-model", "snapshot-key", "snapshot-site", "https://private-upstream.test", "private-encrypted-key") }) };
            db.Runs.Add(run); await db.SaveChangesAsync();
            db.Results.Add(new DetectionResult { RunId = run.Id, Status = "Failed", SiteName = "site", KeyName = "key", ModelName = "failed-model", Error = "有效回答不足", ResponsesJson = """[{"Error":"HTTP 401: invalid key"}]""" });
            await db.SaveChangesAsync();
            db.Results.AddRange(Enumerable.Range(0, 205).Select(i => new DetectionResult { RunId = run.Id, Status = "Success", ModelName = "success-" + i }));
            await db.SaveChangesAsync();
        }
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" });
        var runs = await client.GetFromJsonAsync<JsonElement>("/api/runs");
        var failures = runs[0].GetProperty("failures");
        var target = runs[0].GetProperty("targets")[0];
        Assert.Equal("snapshot-model", target.GetProperty("modelName").GetString());
        Assert.Equal("snapshot-key", target.GetProperty("keyName").GetString());
        Assert.Equal("snapshot-site", target.GetProperty("siteName").GetString());
        Assert.False(target.TryGetProperty("protectedKey", out _));
        Assert.DoesNotContain("private-encrypted-key", runs.GetRawText());
        Assert.DoesNotContain("private-upstream.test", runs.GetRawText());
        Assert.Equal(1, failures.GetArrayLength());
        Assert.Equal("failed-model", failures[0].GetProperty("modelName").GetString());
        Assert.Contains("HTTP 401", failures[0].GetProperty("reason").GetString());
        var history = await client.GetFromJsonAsync<JsonElement>("/api/results");
        Assert.Equal(200, history.GetArrayLength());
        Assert.All(history.EnumerateArray(), item => Assert.Equal("Success", item.GetProperty("status").GetString()));
    }
}
