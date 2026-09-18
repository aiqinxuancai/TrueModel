using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class JuiceTests
{
    [Fact]
    public async Task StartupUpgradesOldResultsAndPersistsJuice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-juice-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "test.db")}";
        await using (var legacy = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(connection).Options))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Results.Add(new DetectionResult { ModelName = "gpt-5", Status = "Success" });
            await legacy.SaveChangesAsync();
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN JuiceValue");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN JuiceStatus");
        }
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = connection,
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            })));
        using var http = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var old = await db.Results.SingleAsync();
        Assert.Null(old.JuiceValue);
        Assert.Null(old.JuiceStatus);
        old.JuiceValue = 128;
        old.JuiceStatus = "Success";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(128, (await db.Results.SingleAsync()).JuiceValue);
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("openai/GPT-5.4", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("claude-sonnet", false)]
    [InlineData("o3", false)]
    [InlineData("notgpt-5", false)]
    public void OnlyGptModelsAreSupported(string model, bool expected) => Assert.Equal(expected, JuiceProbe.Supports(model));

    [Theory]
    [InlineData(" 128\n", 128)]
    [InlineData("0", 0)]
    [InlineData("I cannot provide that", null)]
    [InlineData("128 or 64", null)]
    [InlineData("2 * 10 / 5", null)]
    [InlineData("-1", null)]
    [InlineData("2147483648", null)]
    public void OnlyUnambiguousIntegersAreAccepted(string response, int? expected) => Assert.Equal(expected, JuiceProbe.Parse(response));

    [Theory]
    [InlineData("gpt-5", 3, "128", false, false, "Success")]
    [InlineData("gpt-5", 6, "0", false, false, "Success")]
    [InlineData("gpt-5", 3, "Not available", false, false, "Unavailable")]
    [InlineData("gpt-5", 3, "", true, false, "Failed")]
    [InlineData("gpt-5", 3, "128", false, true, "Success")]
    [InlineData("claude-sonnet", 3, "128", false, false, null)]
    public async Task OneProbeAfterAllChallengesWithIndependentOutcome(string model, int count, string answer, bool failure, bool invalidChallenges, string? status)
    {
        var handler = new ProbeHandler(count, answer, failure, invalidChallenges);
        var client = new ModelTraceClient(new HttpClient(handler));
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        var report = await client.Test("https://example.test", "secret", model, bank, count, CancellationToken.None);
        Assert.Equal(count + (status is null ? 0 : 1), handler.Calls);
        Assert.Equal(count, report.GetProperty("responses").GetArrayLength());
        Assert.Equal(invalidChallenges, report.TryGetProperty("error", out _));
        if (status is null) Assert.False(report.TryGetProperty("juice_status", out _));
        else Assert.Equal(status, report.GetProperty("juice_status").GetString());
        if (status == "Success") Assert.Equal(int.Parse(answer), report.GetProperty("juice_value").GetInt32());
    }

    private sealed class ProbeHandler(int count, string answer, bool failure, bool invalidChallenges) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var prompt = body.GetProperty("messages")[0].GetProperty("content").GetString();
            if (Calls > count)
            {
                Assert.Equal(count + 1, Calls);
                Assert.Equal(JuiceProbe.Prompt, prompt);
                if (failure) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = "Unavailable" }) };
            }
            else Assert.NotEqual(JuiceProbe.Prompt, prompt);
            var text = Calls > count ? answer : invalidChallenges ? "invalid" : string.Join(',', Enumerable.Range(1, 310));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } }) };
        }
    }
}
