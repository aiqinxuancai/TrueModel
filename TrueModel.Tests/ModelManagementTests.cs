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

public class ModelManagementTests
{
    [Fact]
    public async Task FavoritesUpgradeLegacyDatabaseRequireAuthenticationAndPersistAcrossRestart()
    {
        await using var host = new TestHost();
        int id;
        await using (var legacy = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(host.ConnectionString).Options))
        {
            await legacy.Database.EnsureCreatedAsync();
            var site = new Site { Name = "legacy", BaseUrl = "https://models.test", Keys = [new SiteKey { Name = "key", Models = [new MonitoredModel { Name = "gpt" }] }] };
            legacy.Sites.Add(site);
            await legacy.SaveChangesAsync();
            id = site.Keys[0].Models[0].Id;
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE Models DROP COLUMN IsFavorite");
        }
        await host.RefreshToken();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsJsonAsync($"/api/models/{id}/favorite", new { isFavorite = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/api/models/batch-delete", new { ids = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { id }, find = "gpt", replacement = "gpt-new" })).StatusCode);
        await host.Login();
        Assert.False((await host.Models()).Single().GetProperty("isFavorite").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync($"/api/models/{id}/favorite", new { isFavorite = true })).StatusCode);
        await host.Restart();
        await host.Login();
        Assert.True((await host.Models()).Single().GetProperty("isFavorite").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync($"/api/models/{id}/favorite", new { isFavorite = false })).StatusCode);
        Assert.False((await host.Models()).Single().GetProperty("isFavorite").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync("/api/models/99999/favorite", new { isFavorite = true })).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync($"/api/models/{id}/favorite", new { isFavorite = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-delete", new { ids = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { id }, find = "gpt", replacement = "gpt-new" })).StatusCode);
        Assert.Equal("gpt", (await host.Models()).Single().GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReplacementAcrossKeysKeepsFavoritesAndHistoryAndResetsCurrentResults()
    {
        await using var host = new TestHost();
        await host.Login();
        var site = await host.Create("/api/sites", new { name = "site", baseUrl = "https://models.test" });
        var firstKey = await host.Create($"/api/sites/{site}/keys", new { name = "first", value = "secret" });
        var secondKey = await host.Create($"/api/sites/{site}/keys", new { name = "second", value = "secret" });
        var first = await host.Create($"/api/keys/{firstKey}/models", new { name = "old-gpt" });
        var second = await host.Create($"/api/keys/{firstKey}/models", new { name = "old-claude" });
        var otherKey = await host.Create($"/api/keys/{secondKey}/models", new { name = "old-gpt" });
        var kept = await host.Create($"/api/keys/{firstKey}/models", new { name = "kept" });
        await host.WithDb(async db =>
        {
            var model = await db.Models.FindAsync(first);
            model!.IsFavorite = true;
            model.JuiceValue = 100; model.JuiceStatus = "Success"; model.JuiceCheckedAt = DateTime.UtcNow;
            model.CandyStatus = "Success"; model.CandyCheckedAt = DateTime.UtcNow; model.CandyReasoningTokens = 32;
            db.Results.AddRange(new DetectionResult { ModelId = first, ModelName = "old-gpt", Status = "Success" },
                new DetectionResult { ModelId = kept, ModelName = "kept", Status = "Success" });
            await db.SaveChangesAsync();
        });
        var response = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second, otherKey, first }, find = "old-", replacement = "new-" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replaced").GetInt32());
        var models = await host.Models();
        Assert.Equal("new-gpt", models.Single(m => m.GetProperty("id").GetInt32() == first).GetProperty("name").GetString());
        Assert.Equal("new-claude", models.Single(m => m.GetProperty("id").GetInt32() == second).GetProperty("name").GetString());
        Assert.Equal("new-gpt", models.Single(m => m.GetProperty("id").GetInt32() == otherKey).GetProperty("name").GetString());
        Assert.Equal("kept", models.Single(m => m.GetProperty("id").GetInt32() == kept).GetProperty("name").GetString());
        var favorite = models.Single(m => m.GetProperty("id").GetInt32() == first);
        Assert.True(favorite.GetProperty("isFavorite").GetBoolean());
        foreach (var field in new[] { "juiceValue", "juiceStatus", "juiceCheckedAt", "candyStatus", "candyCheckedAt", "candyReasoningTokens" })
            Assert.Equal(JsonValueKind.Null, favorite.GetProperty(field).ValueKind);
        var history = await host.Client.GetFromJsonAsync<JsonElement>("/api/results");
        Assert.Contains(history.EnumerateArray(), r => r.GetProperty("modelName").GetString() == "old-gpt");
        Assert.Equal(kept, (await host.Client.GetFromJsonAsync<JsonElement>("/api/results?perModel=true"))[0].GetProperty("modelId").GetInt32());
        Assert.Equal(1, (await host.Client.GetFromJsonAsync<JsonElement>("/api/results?perModel=true")).GetArrayLength());
        await host.WithDb(async db =>
        {
            db.Results.AddRange(Enumerable.Range(0, 12).Select(i => new DetectionResult { ModelId = first, ModelName = "new-gpt", Status = "Success", LatencyMs = i }));
            await db.SaveChangesAsync();
        });
        var recent = await host.Client.GetFromJsonAsync<JsonElement>("/api/results?perModel=true");
        Assert.Equal(10, recent.EnumerateArray().Count(r => r.GetProperty("modelId").GetInt32() == first));
        Assert.DoesNotContain(recent.EnumerateArray(), r => r.GetProperty("modelName").GetString() == "old-gpt");
        var exact = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "new-gpt", replacement = "gpt", matchWholeId = true });
        Assert.Equal(1, (await exact.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replaced").GetInt32());
        var removePrefix = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { second }, find = "new-", replacement = "" });
        Assert.Equal(HttpStatusCode.OK, removePrefix.StatusCode);
        Assert.Equal("claude", (await host.Models()).Single(m => m.GetProperty("id").GetInt32() == second).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChainedReplacementDoesNotViolateUniqueIndexAndTreatsSearchAsLiteral()
    {
        await using var host = new TestHost();
        await host.Login();
        var key = await host.CreateKey();
        var first = await host.Create($"/api/keys/{key}/models", new { name = "a" });
        var second = await host.Create($"/api/keys/{key}/models", new { name = "aa" });
        var response = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "a", replacement = "aa" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "aa", "aaaa" }, (await host.Models()).Select(m => m.GetProperty("name").GetString()).ToArray());
        var literal = await host.Create($"/api/keys/{key}/models", new { name = "prefix.*-gpt-prefix.*" });
        var replaceLiteral = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { literal }, find = "prefix.*", replacement = "$1" });
        Assert.Equal(HttpStatusCode.OK, replaceLiteral.StatusCode);
        Assert.Equal("$1-gpt-$1", (await host.Models()).Single(m => m.GetProperty("id").GetInt32() == literal).GetProperty("name").GetString());
    }

    [Fact]
    public async Task InvalidOrConflictingReplacementLeavesEntireSelectionUnchanged()
    {
        await using var host = new TestHost();
        await host.Login();
        var key = await host.CreateKey();
        var first = await host.Create($"/api/keys/{key}/models", new { name = "old-a" });
        await host.Create($"/api/keys/{key}/models", new { name = "new-a" });
        var second = await host.Create($"/api/keys/{key}/models", new { name = "old-b" });
        foreach (var ids in new int[]?[] { null, [], [-1], Enumerable.Range(1, 501).ToArray() })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids, find = "old-", replacement = "new-" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-delete", new { ids })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "old-", replacement = "new-" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first }, find = "old-a", replacement = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first }, find = "", replacement = "valid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first }, find = "old-", replacement = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, 99999 }, find = "old-", replacement = "valid-" })).StatusCode);
        // Two selected IDs also cannot converge to the same name.
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "b", replacement = "a" })).StatusCode);
        var noMatch = await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "OLD-", replacement = "valid-" });
        Assert.Equal(0, (await noMatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replaced").GetInt32());
        Assert.Equal(new[] { "old-a", "new-a", "old-b" }, (await host.Models()).Select(m => m.GetProperty("name").GetString()).ToArray());
    }

    [Theory]
    [InlineData("Queued", false)]
    [InlineData("Running", false)]
    [InlineData("Queued", true)]
    [InlineData("Running", true)]
    public async Task ReplacementRejectsModelsWithPendingDetectionOrCandy(string status, bool candy)
    {
        await using var host = new TestHost();
        await host.Login();
        var key = await host.CreateKey();
        var first = await host.Create($"/api/keys/{key}/models", new { name = "old-a" });
        var second = await host.Create($"/api/keys/{key}/models", new { name = "old-b" });
        await host.WithDb(async db =>
        {
            if (candy) (await db.Models.FindAsync(second))!.CandyRefreshStatus = status;
            else db.Runs.Add(new DetectionRun { Status = status, Total = 1, TargetsJson = JsonSerializer.Serialize(new[] { new Target(second, "old-b", "key", "site", "https://models.test", "secret") }) });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first, second }, find = "old-", replacement = "new-" })).StatusCode);
        Assert.Equal(new[] { "old-a", "old-b" }, (await host.Models()).Select(m => m.GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/models/batch-replace", new { ids = new[] { first }, find = "old-", replacement = "new-" })).StatusCode);
    }

    [Fact]
    public async Task BatchDeletionCancelsOnlySelectedTargetsAndKeepsHistoryAndOtherModels()
    {
        await using var host = new TestHost();
        await host.Login();
        var firstKey = await host.CreateKey();
        var secondKey = await host.CreateKey();
        var finished = await host.Create($"/api/keys/{firstKey}/models", new { name = "finished" });
        var active = await host.Create($"/api/keys/{firstKey}/models", new { name = "active" });
        var queued = await host.Create($"/api/keys/{secondKey}/models", new { name = "queued" });
        var kept = await host.Create($"/api/keys/{secondKey}/models", new { name = "kept" });
        int mixedRun = 0, emptyRun = 0;
        await host.WithDb(async db =>
        {
            var run = new DetectionRun { Status = "Running", Total = 3, Completed = 1, TargetsJson = JsonSerializer.Serialize(new[] { finished, active, kept }.Select(id => new Target(id, "model", "key", "site", "https://models.test", "secret"))) };
            var empty = new DetectionRun { Status = "Queued", Total = 1, TargetsJson = JsonSerializer.Serialize(new[] { new Target(queued, "queued", "key", "site", "https://models.test", "secret") }) };
            db.Runs.AddRange(run, empty);
            await db.SaveChangesAsync();
            mixedRun = run.Id; emptyRun = empty.Id;
            db.Results.Add(new DetectionResult { RunId = mixedRun, ModelId = finished, ModelName = "finished", Status = "Success" });
            (await db.Models.FindAsync(active))!.IsFavorite = true;
            await db.SaveChangesAsync();
        });
        var control = host.Factory.Services.GetRequiredService<DetectionControl>();
        using var selectedCancellation = new CancellationTokenSource();
        using var keptCancellation = new CancellationTokenSource();
        control.Active.Add((mixedRun, active), selectedCancellation);
        control.Active.Add((mixedRun, kept), keptCancellation);
        var response = await host.Client.PostAsJsonAsync("/api/models/batch-delete", new { ids = new[] { finished, active, queued, 99999, active } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deleted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, deleted.GetProperty("deleted").GetInt32());
        Assert.Equal(1, deleted.GetProperty("skipped").GetInt32());
        Assert.True(selectedCancellation.IsCancellationRequested);
        Assert.False(keptCancellation.IsCancellationRequested);
        Assert.Equal(kept, (await host.Models()).Single().GetProperty("id").GetInt32());
        await host.WithDb(async db =>
        {
            var mixed = await db.Runs.FindAsync(mixedRun);
            Assert.Equal("Running", mixed!.Status);
            Assert.Equal(2, mixed.Total);
            Assert.Equal(new[] { finished, kept }, JsonSerializer.Deserialize<Target[]>(mixed.TargetsJson)!.Select(t => t.ModelId).ToArray());
            var empty = await db.Runs.FindAsync(emptyRun);
            Assert.Equal("Cancelled", empty!.Status);
            Assert.Equal(0, empty.Total);
            Assert.Equal(1, await db.Results.CountAsync());
            Assert.Equal(2, await db.Sites.CountAsync());
            Assert.Equal(2, await db.Keys.CountAsync());
        });
        var history = await host.Client.GetFromJsonAsync<JsonElement>("/api/results");
        Assert.Equal("finished", history[0].GetProperty("modelName").GetString());
    }

    private sealed class TestHost : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "truemodel-management-" + Guid.NewGuid());
        public string ConnectionString => $"Data Source={Path.Combine(DirectoryPath, "test.db")}";
        public WebApplicationFactory<Program> Factory { get; private set; }
        private HttpClient? client;
        public HttpClient Client => client ??= Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        public TestHost()
        {
            Directory.CreateDirectory(DirectoryPath);
            Factory = NewFactory();
        }

        private WebApplicationFactory<Program> NewFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = DirectoryPath, ["ConnectionStrings:Default"] = ConnectionString,
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(DetectionWorker) || s.ImplementationType == typeof(CandyWorker)).ToArray())
                    services.Remove(descriptor);
            });
        });

        public async Task RefreshToken()
        {
            var session = await Client.GetFromJsonAsync<JsonElement>("/api/session");
            Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }

        public async Task Login()
        {
            await RefreshToken();
            Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" })).StatusCode);
            await RefreshToken();
        }

        public async Task<int> Create(string path, object body)
        {
            var response = await Client.PostAsJsonAsync(path, body);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }

        public async Task<int> CreateKey()
        {
            var site = await Create("/api/sites", new { name = "site", baseUrl = "https://models.test" });
            return await Create($"/api/sites/{site}/keys", new { name = "key", value = "secret" });
        }

        public async Task<JsonElement[]> Models() => (await Client.GetFromJsonAsync<JsonElement>("/api/sites"))
            .EnumerateArray().SelectMany(s => s.GetProperty("keys").EnumerateArray()).SelectMany(k => k.GetProperty("models").EnumerateArray()).ToArray();

        public async Task WithDb(Func<AppDb, Task> action)
        {
            using var scope = Factory.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<AppDb>());
        }

        public async Task Restart()
        {
            client?.Dispose(); client = null;
            await Factory.DisposeAsync();
            Factory = NewFactory();
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
