using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class CraneRidingTests
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 600 400\"><circle cx=\"150\" cy=\"300\" r=\"70\"/><path d=\"M150 300L300 180L450 300Z\" fill=\"red\"/></svg>";
    public static TheoryData<string, string> Languages => new()
    {
        { "zh", "丹顶鹤" }, { "en", "red-crowned crane" }, { "ja", "タンチョウ" }, { "ko", "단정학" },
        { "fr", "grue du Japon" }, { "es", "grulla de corona roja" }, { "de", "Mandschurenkranich" }, { "pt", "grou de coroa vermelha" }
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public void PromptsPreserveTheCraneSubjectInEveryLanguage(string language, string subject)
    {
        var translation = PromptLanguages.Get(language).CraneRiding;
        Assert.Contains(subject, translation);
        Assert.Contains("SVG", translation);
        Assert.Equal(CandyTestPrompts.WithoutWhitespace(translation), CandyTestPrompts.WithoutWhitespace(CraneRidingProbe.CreatePrompt(language)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractsBareAndFencedSvgWithoutChangingTheDrawing(bool fenced)
    {
        var output = CraneRidingProbe.ExtractSvg(fenced ? $"说明\n```svg\n{Svg}\n```\n完成" : Svg);
        Assert.True(XNode.DeepEquals(XElement.Parse(Svg), XElement.Parse(output)));
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", CraneRidingProbe.ExtractSvg("<svg><rect width=\"10\" height=\"10\"/></svg>"));
    }

    [Fact]
    public void RemovesActiveContentAndExternalResourcesButPreservesLocalDrawingStyles()
    {
        var output = CraneRidingProbe.ExtractSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" onload="alert(1)">
              <script>alert(1)</script><foreignObject><iframe src="https://evil.test"/></foreignObject>
              <image href="https://evil.test/pixel"/><use href="https://evil.test/image.svg#x"/>
              <style>@import 'https://evil.test/style.css';</style><style>.bird { fill: red; stroke: black }</style>
              <defs><linearGradient id="red"><stop offset="0" stop-color="red"/></linearGradient><path id="bird" d="M0 0L10 10"/></defs>
              <path d="M0 0L10 10" fill="url(#red)" onclick="alert(2)" style="fill:url(https://evil.test/x)"/>
              <use xlink:href="#bird"/><rect width="10" height="10" fill="red"/>
            </svg>
            """);
        foreach (var removed in new[] { "script", "foreignObject", "iframe", "<image", "onload", "onclick", "evil.test", "@import" }) Assert.DoesNotContain(removed, output);
        Assert.Contains("url(#red)", output);
        Assert.Contains(".bird { fill: red; stroke: black }", output);
        Assert.Contains("href=\"#bird\"", output);
        Assert.Contains("linearGradient", output);
    }

    [Theory]
    [InlineData("抱歉，无法完成。")]
    [InlineData("<svg><path d=\"M0 0\"/>")]
    [InlineData("<svg><path></svg>")]
    [InlineData("<svg xmlns=\"https://evil.test\"><path/></svg>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><svg><text>&x;</text></svg>")]
    public void InvalidOrEmptyDrawingsFailWithAUsefulReason(string reply) =>
        Assert.NotEmpty(Assert.Throws<FormatException>(() => CraneRidingProbe.ExtractSvg(reply)).Message);

    [Fact]
    public async Task ManualRunStreamsTheSvgAndRetainsPreviousSuccessAfterAFailure()
    {
        using var handler = new SvgHandler($"```svg\n{Svg}\n```", hold: true);
        var directory = NewDirectory();
        await using var app = Create(directory, handler);
        using var browser = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/crane-rides/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/crane-rides/1/image.svg")).StatusCode);
        await Login(browser);
        var modelId = await SeedModel(app, "ja");
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync("/api/models/99999/crane-riding", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostAsJsonAsync($"/api/models/{modelId}/crane-riding", new { })).StatusCode);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync($"/api/models/{modelId}/crane-riding", new { })).StatusCode);
        handler.Release.TrySetResult();
        var run = await WaitForCompletion(browser, modelId);
        Assert.Equal("Success", run.GetProperty("status").GetString());
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.StreamRequested);
        Assert.Equal(3, handler.Messages.GetArrayLength());
        Assert.False(run.GetProperty("simulatedContext").GetBoolean());
        Assert.Contains("タンチョウ", CandyTestPrompts.WithoutWhitespace(handler.Prompt));
        Assert.Equal(123, run.GetProperty("reasoningTokens").GetInt64());
        var id = run.GetProperty("id").GetInt32();
        var detail = await browser.GetFromJsonAsync<JsonElement>($"/api/crane-rides/{id}");
        Assert.Equal(handler.Prompt, detail.GetProperty("prompt").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("conversationJson").ValueKind);
        Assert.Equal(handler.Reply, detail.GetProperty("response").GetString());
        var image = await browser.GetAsync($"/api/crane-rides/{id}/image.svg");
        Assert.Equal("image/svg+xml", image.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", image.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(XNode.DeepEquals(XElement.Parse(Svg), XElement.Parse(await image.Content.ReadAsStringAsync())));
        var download = await browser.GetAsync($"/api/crane-rides/{id}/image.svg?download=true");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        handler.Reply = "无法完成，fixture-secret-key";
        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostAsJsonAsync($"/api/models/{modelId}/crane-riding", new { })).StatusCode);
        var failure = await WaitForCompletion(browser, modelId);
        Assert.Equal("Failed", failure.GetProperty("status").GetString());
        Assert.Contains("SVG", failure.GetProperty("error").GetString());
        var failedId = failure.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/crane-rides/{failedId}/image.svg")).StatusCode);
        Assert.DoesNotContain("fixture-secret-key", await browser.GetStringAsync($"/api/crane-rides/{failedId}"));
        var latest = await browser.GetFromJsonAsync<JsonElement>("/api/crane-rides/latest");
        Assert.Equal(id, latest[0].GetProperty("latestSuccessId").GetInt32());
        Assert.Equal(failedId, latest[0].GetProperty("latest").GetProperty("id").GetInt32());
        Assert.Equal(2, latest[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UpgradePersistsSettingsAndQueuedSnapshotWhileRestartInterruptsRunningJobs()
    {
        var directory = NewDirectory();
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options;
        await using (var legacy = new AppDb(options))
        {
            await legacy.Database.EnsureCreatedAsync();
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Settings DROP COLUMN CraneRidingLanguage");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Settings DROP COLUMN CraneRidingSimulatedContext");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE CraneRidingRuns");
            legacy.WorldMaps.Add(new WorldMapRun { ModelId = 999, ModelName = "old map", Status = "Queued" });
            await legacy.SaveChangesAsync();
        }
        using var handler = new SvgHandler(Svg, stream: false);
        int modelId, interruptedId;
        await using (var app = Create(directory, handler, worker: false))
        {
            using var browser = app.CreateClient();
            await Login(browser);
            Assert.Equal("zh", (await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingLanguage").GetString());
            Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingSimulatedContext").GetBoolean());
            foreach (var language in new[] { "zh", "en", "ja", "ko", "fr", "es", "de", "pt" })
                Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingLanguage = language })).StatusCode);
            foreach (var invalid in new string?[] { null, "", "xx" })
                Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingLanguage = invalid })).StatusCode);
            modelId = await SeedModel(app, "ja");
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingSimulatedContext = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { challengeCount = 4 })).StatusCode);
            Assert.True((await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingSimulatedContext").GetBoolean());
            Assert.Equal(HttpStatusCode.Accepted, (await browser.PostAsJsonAsync($"/api/models/{modelId}/crane-riding", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync($"/api/models/{modelId}/crane-riding", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingLanguage = "de", craneRidingSimulatedContext = false })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { challengeCount = 4 })).StatusCode);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            Assert.Equal("ja", (await db.CraneRides.SingleAsync()).Language);
            Assert.True((await db.CraneRides.SingleAsync()).SimulatedContext);
            Assert.Equal("Queued", (await db.WorldMaps.SingleAsync()).Status);
            var interrupted = new CraneRidingRun { ModelId = modelId + 1, Status = "Running" };
            db.CraneRides.Add(interrupted);
            await db.SaveChangesAsync();
            interruptedId = interrupted.Id;
        }
        // Give the interrupted run a surviving model so normal orphan cleanup retains it.
        await using (var db = new AppDb(options))
        {
            var keyId = await db.Keys.Select(k => k.Id).SingleAsync();
            db.Models.Add(new MonitoredModel { Id = modelId + 1, SiteKeyId = keyId, Name = "interrupted" });
            await db.SaveChangesAsync();
        }
        await using (var app = Create(directory, handler))
        {
            using var browser = app.CreateClient();
            await Login(browser);
            var run = await WaitForCompletion(browser, modelId);
            Assert.Equal("Success", run.GetProperty("status").GetString());
            Assert.Equal("ja", run.GetProperty("language").GetString());
            Assert.True(run.GetProperty("simulatedContext").GetBoolean());
            Assert.True((handler.Messages.GetArrayLength() - 1) / 2 >= 64);
            Assert.True(handler.Messages.EnumerateArray().SkipLast(1).Sum(m => m.GetProperty("content").GetString()!.Length) >= 60_000);
            var detail = await browser.GetFromJsonAsync<JsonElement>($"/api/crane-rides/{run.GetProperty("id").GetInt32()}");
            Assert.Equal(handler.Messages.GetRawText(), detail.GetProperty("conversationJson").GetString());
            Assert.Equal(handler.Prompt, detail.GetProperty("prompt").GetString());
            Assert.True(handler.StreamRequested);
            Assert.Equal(1, handler.Calls);
            Assert.Contains("タンチョウ", CandyTestPrompts.WithoutWhitespace(handler.Prompt));
            Assert.Equal("de", (await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingLanguage").GetString());
            Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingSimulatedContext").GetBoolean());
            Assert.Equal("Interrupted", (await browser.GetFromJsonAsync<JsonElement>($"/api/crane-rides/{interruptedId}")).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task ExistingCraneHistoryUpgradesWithSimulatedContextDisabled()
    {
        var directory = NewDirectory();
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options;
        await using (var legacy = new AppDb(options))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.CraneRides.Add(new CraneRidingRun { Status = "Success", Svg = Svg, Prompt = "old question" });
            await legacy.SaveChangesAsync();
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE CraneRidingRuns DROP COLUMN SimulatedContext");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE CraneRidingRuns DROP COLUMN ConversationJson");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Settings DROP COLUMN CraneRidingSimulatedContext");
        }
        using var handler = new SvgHandler(Svg);
        await using var app = Create(directory, handler, worker: false);
        using var browser = app.CreateClient();
        await Login(browser);
        var detail = await browser.GetFromJsonAsync<JsonElement>("/api/crane-rides/1");
        Assert.Equal("Success", detail.GetProperty("status").GetString());
        Assert.Equal("old question", detail.GetProperty("prompt").GetString());
        Assert.Equal(Svg, detail.GetProperty("svg").GetString());
        Assert.False(detail.GetProperty("simulatedContext").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("conversationJson").ValueKind);
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingSimulatedContext").GetBoolean());
        foreach (var enabled in new[] { true, false })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingSimulatedContext = enabled })).StatusCode);
            Assert.Equal(enabled, (await browser.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("craneRidingSimulatedContext").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PutAsJsonAsync("/api/settings", new { craneRidingSimulatedContext = "true" })).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HistoryPruningKeepsOnlyTwentyRecentRunsPerExistingModel()
    {
        var directory = NewDirectory();
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options);
        await db.Database.EnsureCreatedAsync();
        var site = new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "a" }] }] };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var modelId = site.Keys[0].Models[0].Id;
        var runs = Enumerable.Range(0, 25).Select(_ => new CraneRidingRun { ModelId = modelId, Status = "Success", Svg = Svg }).ToArray();
        db.CraneRides.AddRange(runs);
        db.CraneRides.Add(new CraneRidingRun { ModelId = 999, Status = "Failed" });
        await db.SaveChangesAsync();
        await CraneRidingProbe.Prune(db);
        Assert.Equal(runs.Skip(5).Select(r => r.Id), await db.CraneRides.OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync());
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-crane-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static WebApplicationFactory<Program> Create(string directory, SvgHandler handler, bool worker = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(WorldMapWorker) || s.ImplementationType == typeof(DetectionWorker) ||
                    s.ImplementationType == typeof(CandyWorker) || !worker && s.ImplementationType == typeof(CraneRidingWorker)).ToArray()) services.Remove(descriptor);
                services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

    private static async Task<int> SeedModel(WebApplicationFactory<Program> app, string language)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ApiKeys.v1");
        var site = new Site { Name = "s", BaseUrl = "https://fixture.test", Keys = [new SiteKey { Name = "k", ProtectedValue = protector.Protect("fixture-secret-key"), Models = [new MonitoredModel { Name = "model" }] }] };
        db.Sites.Add(site);
        (await db.Settings.SingleAsync()).CraneRidingLanguage = language;
        await db.SaveChangesAsync();
        return site.Keys[0].Models[0].Id;
    }

    private static async Task<JsonElement> WaitForCompletion(HttpClient browser, int modelId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var history = await browser.GetFromJsonAsync<JsonElement>($"/api/models/{modelId}/crane-rides", deadline.Token);
            if (history.GetArrayLength() > 0 && history[0].GetProperty("status").GetString() is not ("Queued" or "Running")) return history[0];
            await Task.Delay(100, deadline.Token);
        }
    }

    private static async Task Login(HttpClient client)
    {
        async Task Csrf()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await Csrf();
        (await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).EnsureSuccessStatusCode();
        await Csrf();
    }

    private sealed class SvgHandler(string reply, bool hold = false, bool stream = true) : HttpMessageHandler
    {
        public string Reply = reply;
        public string Prompt = "";
        public JsonElement Messages;
        public int Calls;
        public bool StreamRequested;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Messages = body.GetProperty("messages").Clone();
            Prompt = Messages[Messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            StreamRequested = body.GetProperty("stream").GetBoolean();
            Started.TrySetResult();
            if (hold) await Release.Task.WaitAsync(token);
            var payload = stream
                ? "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Reply }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = 123 } } }) + "\n\ndata: [DONE]\n\n"
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Reply }, finish_reason = "stop" } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
