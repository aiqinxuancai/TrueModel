using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TrueModel;

namespace TrueModel.Tests;

public class WorldMapTests
{
    // Stdlib only: CI runners and desktop installs may lack numpy/Pillow.
    private const string Script = """
        import csv
        rows = []
        for lat in range(89, -90, -2):
            rows.append([1 if lat < -65 or (lat > 30 and lon > 0) else 0 for lon in range(-179, 180, 2)])
        with open("world_map_matrix.csv", "w", newline="") as f:
            csv.writer(f).writerows(rows)
        print("ok", sum(map(sum, rows)))
        """;

    public static TheoryData<string> Languages => new() { "zh", "en", "ja", "ko", "fr", "es", "de", "pt" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageKeepsTheDeliveryContractIntact(string language)
    {
        var translation = PromptLanguages.Get(language);
        var prompt = WorldMapProbe.CreatePrompt(language);
        Assert.EndsWith("\n\n" + translation.WorldMapDelivery, prompt);
        Assert.Contains("```python", translation.WorldMapDelivery);
        Assert.Contains(WorldMapProbe.MatrixFile, translation.WorldMapDelivery);
        Assert.Equal(CandyTestPrompts.WithoutWhitespace(translation.WorldMap), CandyTestPrompts.WithoutWhitespace(prompt[..^translation.WorldMapDelivery.Length]));
        foreach (var number in new[] { "89", "-89", "-179", "179", "180", "90", "1440", "720" }) Assert.Contains(number, translation.WorldMap);
    }

    [Fact]
    public void ExtractsTheLongestPythonBlock()
    {
        var response = "说明\n```bash\npython world_map.py\n```\n```python\nprint(1)\n```\n正文\n```python\nimport csv\nprint('full script')\n```";
        Assert.Equal("import csv\nprint('full script')", WorldMapProbe.ExtractScript(response));
        Assert.Equal("import math\nx = 1", WorldMapProbe.ExtractScript("```\nimport math\nx = 1\n```"));
        Assert.Equal("import csv\nprint(2)", WorldMapProbe.ExtractScript("import csv\nprint(2)"));
        Assert.Null(WorldMapProbe.ExtractScript("抱歉，我无法完成这个任务。"));
    }

    [Fact]
    public void ParsesOnlyComplete01Matrices()
    {
        var csv = string.Join("\r\n", Enumerable.Repeat(string.Join(", ", Enumerable.Repeat("1", 180)), 90)) + "\r\n\r\n";
        Assert.All(WorldMapProbe.ParseMatrix((char)0xFEFF + csv), cell => Assert.Equal(1, cell));
        Assert.Contains("89 行", Assert.Throws<FormatException>(() => WorldMapProbe.ParseMatrix(string.Join("\n", csv.Split("\r\n").Skip(1)))).Message);
        Assert.Contains("实际为 179 个", Assert.Throws<FormatException>(() => WorldMapProbe.ParseMatrix(csv.Replace("1, 1\r\n", "1\r\n"))).Message);
        Assert.Contains("不是 0 或 1", Assert.Throws<FormatException>(() => WorldMapProbe.ParseMatrix(csv.Replace("1, 1", "1, 0.5"))).Message);
        Assert.Contains("不是 0 或 1", Assert.Throws<FormatException>(() => WorldMapProbe.ParseMatrix(csv.Replace("1, 1", "1, "))).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void EncodesNearestNeighbourGrayscalePng(int scale)
    {
        var cells = new byte[16200];
        cells[0] = 1;
        var png = WorldMapProbe.EncodePng(cells, scale);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(180 * scale, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(90 * scale, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(cells, WorldMapProbe.ParseMatrix(WorldMapProbe.FormatMatrix(cells)));
    }

    [Fact]
    public async Task PruneKeepsTwentyRunsPerModelAndDeletesTheirFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-worldmap-prune-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options;
        await using var db = new AppDb(options);
        await db.Database.EnsureCreatedAsync();
        var site = new Site { Keys = [new SiteKey { Models = [new MonitoredModel { Name = "a" }] }] };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var modelId = site.Keys[0].Models[0].Id;
        var runs = Enumerable.Range(0, 25).Select(_ => new WorldMapRun { ModelId = modelId, ModelName = "a", Status = "Success" })
            .Append(new WorldMapRun { ModelId = 999, ModelName = "deleted", Status = "Success" }).ToArray();
        db.WorldMaps.AddRange(runs);
        await db.SaveChangesAsync();
        var store = new WorldMapStore(Path.Combine(directory, "maps"));
        foreach (var run in runs) await store.Save(run.Id, new byte[16200], CancellationToken.None);
        File.WriteAllText(Path.Combine(store.Directory, "12345.csv"), "orphan");

        await store.Prune(db);

        var kept = await db.WorldMaps.Select(w => w.Id).OrderBy(id => id).ToArrayAsync();
        Assert.Equal(runs.Take(25).Skip(5).Select(r => r.Id), kept);
        Assert.Equal(kept.SelectMany(id => new[] { $"{id}.csv", $"{id}.png" }).Order(), Directory.EnumerateFiles(store.Directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task ManualRunExecutesTheScriptAndServesTheMap()
    {
        var python = await new PythonSandbox(new ConfigurationBuilder().Build(), NullLogger<PythonSandbox>.Instance).Find(CancellationToken.None);
        if (python is null) return; // Python is an optional runtime outside Docker.
        var handler = new ScriptHandler($"方法：手写轮廓。\n```python\n{Script}\n```");
        await using var app = Create(handler, out var directory);
        using var browser = app.CreateClient();
        await Login(browser);
        var id = await SeedModel(app, "ja");

        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostAsJsonAsync($"/api/models/{id}/world-map", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync($"/api/models/{id}/world-map", new { })).StatusCode);
        var run = await WaitForCompletion(browser, id);

        Assert.True(run.GetProperty("status").GetString() == "Success", run.ToString());
        Assert.Equal(1, handler.Calls);
        Assert.Contains("world_map_matrix.csv", handler.Prompt);
        Assert.Contains("サンプル", handler.Prompt);
        var land = Enumerable.Range(0, 90).Sum(row => 89 - row * 2 < -65 ? 180 : 89 - row * 2 > 30 ? 90 : 0);
        Assert.Equal(land, run.GetProperty("landCount").GetInt32());
        var detail = await browser.GetFromJsonAsync<JsonElement>($"/api/world-maps/{run.GetProperty("id").GetInt32()}");
        Assert.Contains("ok", detail.GetProperty("output").GetString());
        Assert.Equal(Script.Trim(), detail.GetProperty("script").GetString());
        var image = await browser.GetAsync($"/api/world-maps/{run.GetProperty("id").GetInt32()}/image?scale=8");
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1440, BinaryPrimitives.ReadInt32BigEndian((await image.Content.ReadAsByteArrayAsync()).AsSpan(16)));
        var csv = await browser.GetStringAsync($"/api/world-maps/{run.GetProperty("id").GetInt32()}/matrix.csv");
        Assert.Equal(land, WorldMapProbe.ParseMatrix(csv).Count(c => c == 1));
        var latest = await browser.GetFromJsonAsync<JsonElement>("/api/world-maps/latest");
        Assert.Equal(run.GetProperty("id").GetInt32(), latest[0].GetProperty("latestSuccessId").GetInt32());
    }

    [Fact]
    public async Task MissingPythonFailsWithoutCallingTheModel()
    {
        var handler = new ScriptHandler("```python\nprint(1)\n```");
        await using var app = Create(handler, out _, python: Path.Combine(Path.GetTempPath(), "no-such-python-" + Guid.NewGuid()));
        using var browser = app.CreateClient();
        await Login(browser);
        var id = await SeedModel(app, "zh");
        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostAsJsonAsync($"/api/models/{id}/world-map", new { })).StatusCode);
        var run = await WaitForCompletion(browser, id);
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Contains("Python", run.GetProperty("error").GetString());
        Assert.Equal(0, handler.Calls);
    }

    private static WebApplicationFactory<Program> Create(ScriptHandler handler, out string directory, string? python = null)
    {
        var data = directory = Path.Combine(Path.GetTempPath(), "truemodel-worldmap-web-" + Guid.NewGuid());
        Directory.CreateDirectory(data);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = data, ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(data, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning",
                ["WorldMap:Python"] = python
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });
    }

    private static async Task<int> SeedModel(WebApplicationFactory<Program> app, string language)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ApiKeys.v1");
        var site = new Site { Name = "s", BaseUrl = "https://fixture.test", Keys = [new SiteKey { Name = "k", ProtectedValue = protector.Protect("secret"), Models = [new MonitoredModel { Name = "model" }] }] };
        db.Sites.Add(site);
        (await db.Settings.SingleAsync()).WorldMapLanguage = language;
        await db.SaveChangesAsync();
        return site.Keys[0].Models[0].Id;
    }

    private static async Task<JsonElement> WaitForCompletion(HttpClient browser, int modelId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var history = await browser.GetFromJsonAsync<JsonElement>($"/api/models/{modelId}/world-maps", deadline.Token);
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

    private sealed class ScriptHandler(string reply) : HttpMessageHandler
    {
        public int Calls;
        public string Prompt = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Prompt = TestConversation.Prompt(await request.Content!.ReadFromJsonAsync<JsonElement>(token));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = reply }, finish_reason = "stop" } } }) };
        }
    }
}
