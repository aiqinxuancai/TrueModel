using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class ModelDiscoveryTests
{
    [Theory]
    [InlineData("https://models.test", "https://models.test/v1/models")]
    [InlineData("https://models.test/v1/", "https://models.test/v1/models")]
    [InlineData("https://models.test/proxy/v1", "https://models.test/proxy/v1/models")]
    [InlineData("https://models.test/v1/chat/completions", "https://models.test/v1/models")]
    [InlineData("https://models.test/v1/models/", "https://models.test/v1/models")]
    public void BuildsModelsEndpoint(string input, string expected) => Assert.Equal(expected, ModelDiscovery.ModelsUrl(input));

    [Fact]
    public async Task UsesBearerKeyAndNormalizesModels()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://models.test/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"z-model"},{"id":"a-model"},{"id":" a-model "},{"id":""},{"id":7},{},null]}""") });
        });
        using var client = new HttpClient(handler);
        Assert.Equal(["a-model", "z-model"], await new ModelDiscovery(client).Fetch("https://models.test", "test-secret", CancellationToken.None));
    }

    [Theory]
    [InlineData(401, "test-secret", "HTTP 401")]
    [InlineData(404, "test-secret", "HTTP 404")]
    [InlineData(302, "test-secret", "HTTP 302")]
    [InlineData(200, "<html>test-secret</html>", "JSON")]
    [InlineData(200, "{\"data\":{}}", "data")]
    public async Task ReportsErrorsWithoutReturningUpstreamBody(int status, string body, string expected)
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) })));
        var error = await Assert.ThrowsAsync<ModelDiscoveryException>(() => new ModelDiscovery(client).Fetch("https://models.test", "test-secret", CancellationToken.None));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
    }

    [Fact]
    public async Task DistinguishesTimeoutFromCallerCancellation()
    {
        using var client = new HttpClient(new StubHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true))));
        var service = new ModelDiscovery(client);
        Assert.Contains("超时", (await Assert.ThrowsAsync<ModelDiscoveryException>(() => service.Fetch("https://models.test", "key", CancellationToken.None))).Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Fetch("https://models.test", "key", new CancellationToken(true)));
    }

    [Fact]
    public async Task DiscoveryRequiresAuthenticationAndBatchAddsOnlySelectedModels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-models-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var requests = 0;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddHttpClient<ModelDiscovery>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, _) =>
            {
                requests++;
                Assert.Equal("private-api-key", request.Headers.Authorization!.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "alpha" }, new { id = "beta" } } }) });
            })));
        });
        using var client = factory.CreateClient();
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await RefreshToken();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/keys/1/discover-models", new { })).StatusCode);
        Assert.Equal(0, requests);
        await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" });
        await RefreshToken();
        var site = await (await client.PostAsJsonAsync("/api/sites", new { name = "site", baseUrl = "https://models.test" })).Content.ReadFromJsonAsync<JsonElement>();
        var key = await (await client.PostAsJsonAsync($"/api/sites/{site.GetProperty("id")}/keys", new { name = "key", value = "private-api-key" })).Content.ReadFromJsonAsync<JsonElement>();
        var route = $"/api/keys/{key.GetProperty("id")}";
        var discovery = await client.PostAsJsonAsync(route + "/discover-models", new { });
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.DoesNotContain("private-api-key", await discovery.Content.ReadAsStringAsync());
        Assert.Equal(1, requests);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/sites"))[0].GetProperty("keys")[0].GetProperty("models").GetArrayLength());
        var added = await (await client.PostAsJsonAsync(route + "/models/batch", new { names = new[] { "alpha", "alpha" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, added.GetProperty("added").GetInt32());
        var repeated = await (await client.PostAsJsonAsync(route + "/models/batch", new { names = new[] { "alpha", "manual-model" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, repeated.GetProperty("added").GetInt32());
        Assert.Equal(1, repeated.GetProperty("skipped").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route + "/models/batch", new { names = new[] { "valid", " " } })).StatusCode);
        var models = (await client.GetFromJsonAsync<JsonElement>("/api/sites"))[0].GetProperty("keys")[0].GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["alpha", "manual-model"], models);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/keys/99999/discover-models", new { })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route + "/discover-models", new { })).StatusCode);
        Assert.Equal(1, requests);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
