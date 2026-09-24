using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class JuiceTests
{
    [Fact]
    public async Task StartupUpgradesOldResultsAndPersistsJuiceAndCandy()
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
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN JuicePrompt");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE JuiceMethodStatistics");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN JuiceValue");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN JuiceStatus");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN JuicePrompt");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN JuiceCheckedAt");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN CandyStatus");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN CandyPrompt");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN CandyResponse");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN CandyError");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN CandyStatus");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN CandyPrompt");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN CandyResponse");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results DROP COLUMN CandyError");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN CandyCheckedAt");
            // Existing yes/no results must not become candy passes during the upgrade.
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results ADD COLUMN InstructionStatus TEXT NULL");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Results ADD COLUMN InstructionResponse TEXT NULL");
            await legacy.Database.ExecuteSqlRawAsync("UPDATE Results SET InstructionStatus = 'Success', InstructionResponse = '是。'");
        }
        var standalone = new ProbeHandler(0, "96", false, false) { ExpectedFirst = "direct-only" };
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = connection,
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => standalone));
        });
        using var http = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var old = await db.Results.SingleAsync();
        Assert.Null(old.JuiceValue);
        Assert.Null(old.JuiceStatus);
        Assert.Null(old.JuicePrompt);
        Assert.Null(old.CandyStatus);
        Assert.Null(old.CandyPrompt);
        Assert.Null(old.CandyResponse);
        Assert.Null(old.CandyError);
        old.JuicePrompt = "historical prompt";
        old.JuiceValue = 128;
        old.JuiceStatus = "Success";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(128, (await db.Results.SingleAsync()).JuiceValue);
        var history = factory.Services.GetRequiredService<JuiceHistory>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => history.Record("endpoint", "gpt-5", "direct-only", true, CancellationToken.None)));
        await history.Record("endpoint", "gpt-5", "direct", false, CancellationToken.None);
        var freshHistory = new JuiceHistory(factory.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal("direct-only", (await freshHistory.Ranked("endpoint", "gpt-5", CancellationToken.None))[0].Id);
        Assert.Equal("xml", (await freshHistory.Ranked("other-endpoint", "gpt-5", CancellationToken.None))[0].Id);
        Assert.Equal("xml", (await freshHistory.Ranked("endpoint", "gpt-4", CancellationToken.None))[0].Id);
        var stat = await db.JuiceMethodStatistics.SingleAsync(s => s.Method == "direct-only");
        Assert.Equal(8, stat.Attempts);
        Assert.Equal(8, stat.Successes);
        var handler = new ProbeHandler(3, "64", false, false) { ExpectedFirst = "direct-only" };
        var engine = new ModelTraceClient(new HttpClient(handler), freshHistory);
        var endpoint = ModelTraceClient.CompletionUrl("https://example.test", false);
        await history.Record(endpoint, "gpt-5", "direct-only", true, CancellationToken.None);
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        var report = await engine.Test("https://example.test", "secret", "gpt-5", bank, 3, CancellationToken.None);
        Assert.Equal("direct-only", report.GetProperty("juice_method").GetString());
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ApiKeys.v1");
        var site = new Site { Name = "test", BaseUrl = "https://example.test", Keys = [new SiteKey { ProtectedValue = protector.Protect("secret"), Models = [new MonitoredModel { Name = "gpt-5" }, new MonitoredModel { Name = "claude" }] }] };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var model = site.Keys[0].Models[0];
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync($"/api/models/{model.Id}/juice", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync($"/api/models/{model.Id}/candy", new { })).StatusCode);
        async Task Csrf()
        {
            var session = await http.GetFromJsonAsync<JsonElement>("/api/session");
            http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await Csrf();
        (await http.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
        await Csrf();
        var methods = await http.GetFromJsonAsync<JuiceMethod[]>("/api/juice-methods");
        Assert.Equal(JuiceProbe.Methods.Count, methods!.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"/api/models/{model.Id}/juice?methodId=unknown", new { })).StatusCode);
        var refresh = await http.PostAsJsonAsync($"/api/models/{model.Id}/juice", new { });
        refresh.EnsureSuccessStatusCode();
        Assert.Equal(96, (await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("juiceValue").GetInt32());
        Assert.Equal(1, standalone.Calls);
        db.ChangeTracker.Clear();
        Assert.Equal(96, (await db.Models.FindAsync(model.Id))!.JuiceValue);
        Assert.NotNull((await db.Models.FindAsync(model.Id))!.JuiceCheckedAt);
        Assert.Equal(JuiceProbe.Methods.Single(m => m.Id == "direct-only").Prompt, (await db.Models.FindAsync(model.Id))!.JuicePrompt);
        Assert.Equal("historical prompt", (await db.Results.SingleAsync()).JuicePrompt);
        Assert.Equal(128, (await db.Results.SingleAsync()).JuiceValue);
        Assert.Empty(await db.Runs.ToArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync($"/api/models/{site.Keys[0].Models[1].Id}/juice", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/models/999999/juice", new { })).StatusCode);
        Assert.Equal(1, standalone.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/models/999999/candy", new { })).StatusCode);
        var candyRefresh = await http.PostAsJsonAsync($"/api/models/{model.Id}/candy", new { });
        candyRefresh.EnsureSuccessStatusCode();
        Assert.Equal("Success", (await candyRefresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candyStatus").GetString());
        db.ChangeTracker.Clear();
        var saved = (await db.Models.FindAsync(model.Id))!;
        Assert.Equal("Success", saved.CandyStatus);
        Assert.Equal("最少取出 21 个糖果。", saved.CandyResponse);
        Assert.Equal(CandyProbe.Prompt, saved.CandyPrompt);
        Assert.NotNull(saved.CandyCheckedAt);
        Assert.Null((await db.Results.SingleAsync()).CandyStatus);
        var sites = await http.GetFromJsonAsync<JsonElement>("/api/sites");
        var publicModel = sites[0].GetProperty("keys")[0].GetProperty("models")[0];
        Assert.Equal("Success", publicModel.GetProperty("candyStatus").GetString());
        Assert.False(publicModel.TryGetProperty("instructionStatus", out _));
        var results = await http.GetFromJsonAsync<JsonElement>("/api/results");
        Assert.Equal(JsonValueKind.Null, results[0].GetProperty("candyStatus").ValueKind);
        Assert.False(results[0].TryGetProperty("instructionStatus", out _));
        Assert.Empty(await db.Runs.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedPromptRunsOnlyOnceEvenWhenRefused(bool refuse)
    {
        var handler = new ProbeHandler(0, "64", false, false) { ExpectedFirst = "chinese-only", RefuseFirst = refuse };
        var engine = new ModelTraceClient(new HttpClient(handler));
        var result = await engine.ProbeJuice("https://example.test", "secret", "gpt-5", CancellationToken.None, "chinese-only");
        Assert.Equal(1, handler.Calls);
        Assert.Equal(refuse ? "Unavailable" : "Success", result["juice_status"]);
        Assert.Equal(JuiceProbe.Methods.Single(m => m.Id == "chinese-only").Prompt, result["juice_prompt"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalFallsBackAndStopsOnFirstInteger(bool structured)
    {
        var handler = new ProbeHandler(3, "64", false, false) { RefuseFirst = true, StructuredRefusal = structured };
        var engine = new ModelTraceClient(new HttpClient(handler));
        var bank = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        var report = await engine.Test("https://example.test", "secret", "gpt-5", bank, 3, CancellationToken.None);
        Assert.Equal(6, handler.Calls);
        Assert.Equal(64, report.GetProperty("juice_value").GetInt32());
        Assert.Equal("direct", report.GetProperty("juice_method").GetString());
        Assert.Equal(JuiceProbe.Prompt, report.GetProperty("juice_prompt").GetString());
    }

    [Fact]
    public void EachWordingHasIndependentRateAndHighestRateWins()
    {
        Assert.Equal(JuiceProbe.Methods.Count, JuiceProbe.Methods.Select(m => m.Id).Distinct().Count());
        Assert.Equal(JuiceProbe.Methods.Count, JuiceProbe.Methods.Select(m => m.Prompt).Distinct().Count());
        var ranked = JuiceProbe.Rank(new[]
        {
            new JuiceMethodStatistic { Method = "direct", Attempts = 100, Successes = 80 },
            new JuiceMethodStatistic { Method = "direct-only", Attempts = 10, Successes = 9 },
            new JuiceMethodStatistic { Method = "direct-instant", Attempts = 5, Successes = 1 }
        });
        Assert.Equal(new[] { "direct-only", "direct", "direct-instant" }, ranked.Take(3).Select(m => m.Id));
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
        Assert.Equal(count + 1 + (status is null ? 0 : status == "Unavailable" ? JuiceProbe.Methods.Count : 1), handler.Calls);
        Assert.Equal(count, report.GetProperty("responses").GetArrayLength());
        Assert.Equal(invalidChallenges, report.TryGetProperty("error", out _));
        Assert.Equal("Success", report.GetProperty("candy").GetProperty("CandyStatus").GetString());
        if (status is null) Assert.False(report.TryGetProperty("juice_status", out _));
        else Assert.Equal(status, report.GetProperty("juice_status").GetString());
        if (status == "Success") Assert.Equal(int.Parse(answer), report.GetProperty("juice_value").GetInt32());
    }

    private sealed class ProbeHandler(int count, string answer, bool failure, bool invalidChallenges) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool RefuseFirst { get; init; }
        public bool StructuredRefusal { get; init; }
        public string? ExpectedFirst { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            var prompt = body.GetProperty("messages")[0].GetProperty("content").GetString();
            if (prompt == CandyProbe.Prompt)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "最少取出 21 个糖果。" }, finish_reason = "stop" } } }) };
            if (Calls > count)
            {
                Assert.Equal(ExpectedFirst is null ? JuiceProbe.Methods[Calls - count - 1].Prompt : JuiceProbe.Methods.Single(m => m.Id == ExpectedFirst).Prompt, prompt);
                if (RefuseFirst && Calls == count + 1)
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = StructuredRefusal ? null : "I cannot provide that.", refusal = StructuredRefusal ? "I cannot provide that." : null }, finish_reason = "stop" } } }) };
                if (failure) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = "Unavailable" }) };
            }
            else Assert.DoesNotContain(JuiceProbe.Methods, m => m.Prompt == prompt);
            var text = Calls > count ? answer : invalidChallenges ? "invalid" : string.Join(',', Enumerable.Range(1, 310));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } }) };
        }
    }
}
