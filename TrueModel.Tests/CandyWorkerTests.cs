using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class CandyWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshSurvivesClientDisposalAndRejectsDuplicates(bool bulk)
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-candy-worker-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var handler = new BlockingHandler();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory, ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelTraceClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var browser = factory.CreateClient();
        await Login(browser);
        int id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("ApiKeys.v1");
            var site = new Site { BaseUrl = "https://fixture.test", Keys = [new SiteKey { ProtectedValue = protector.Protect("secret"), Models = [new MonitoredModel { Name = "model" }] }] };
            db.Sites.Add(site);
            (await db.Settings.SingleAsync()).CandyReasoningEffort = "high";
            await db.SaveChangesAsync();
            id = site.Keys[0].Models[0].Id;
        }
        using var startToken = new CancellationTokenSource();
        var accepted = await browser.PostAsJsonAsync(bulk ? "/api/candy/refresh-all" : $"/api/models/{id}/candy", new { }, startToken.Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        startToken.Cancel();
        browser.Dispose();
        using var reopened = factory.CreateClient();
        await Login(reopened);
        var sites = await reopened.GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.Equal("Running", sites[0].GetProperty("keys")[0].GetProperty("models")[0].GetProperty("candyRefreshStatus").GetString());
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => reopened.PostAsJsonAsync($"/api/models/{id}/candy", new { })));
        Assert.All(duplicates, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        var repeatedBulk = await reopened.PostAsJsonAsync("/api/candy/refresh-all", new { });
        Assert.Equal(0, (await repeatedBulk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());
        Assert.False(handler.Cancelled);
        Assert.Equal(1, handler.Calls);
        handler.Release.TrySetResult();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            while (await db.Models.AsNoTracking().AnyAsync(m => m.Id == id && m.CandyRefreshStatus == "Running", deadline.Token))
                await Task.Delay(50, deadline.Token);
            var model = await db.Models.AsNoTracking().SingleAsync(deadline.Token);
            Assert.Equal("Completed", model.CandyRefreshStatus);
            Assert.Equal("Success", model.CandyStatus);
            Assert.Equal("high", model.CandyReasoningEffort);
            Assert.Equal(512, model.CandyReasoningTokens);
            Assert.NotNull(model.CandyCheckedAt);
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal(CandyProbe.Prompt, body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = "21" }, finish_reason = "stop" } }, usage = new { completion_tokens_details = new { reasoning_tokens = 512 } } }) };
        }
    }
}
