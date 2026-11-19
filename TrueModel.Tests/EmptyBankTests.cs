using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TrueModel;

namespace TrueModel.Tests;

public class EmptyBankTests
{
    [Fact]
    public async Task EmptyBankCanBeExportedImportedAndEnabledWithOneModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            })));
        using var client = factory.CreateClient();
        async Task RefreshToken()
        {
            var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("token").GetString());
        }
        await RefreshToken();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/banks/empty", new { name = "空库" })).StatusCode);
        await client.PostAsJsonAsync("/api/login", new { username = "admin", password = "test-password-123!" });
        await RefreshToken();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/banks/empty", new { name = " " })).StatusCode);
        var created = await client.PostAsJsonAsync("/api/banks/empty", new { name = " 空库 " });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var route = $"/api/banks/{id}";
        var detail = await client.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal("空库", detail.GetProperty("name").GetString());
        Assert.Empty(detail.GetProperty("models").EnumerateArray());
        Assert.False(detail.GetProperty("active").GetBoolean());
        var json = await client.GetStringAsync(route + "/export");
        Attribution.Validate(json, allowIncomplete: true);
        Assert.Throws<FormatException>(() => Attribution.Validate(json));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/banks", new { name = "导入空库", json })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route + "/activate", new { })).StatusCode);
        var counts = Enumerable.Repeat(1, 355).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route + "/models", new { model = "first", displayName = "first", family = "custom", counts })).StatusCode);
        Attribution.Validate(await client.GetStringAsync(route + "/export"), allowIncomplete: true);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(route + "/activate", new { })).StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>(route)).GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(route + "/models/first")).StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>(route)).GetProperty("active").GetBoolean());
    }
}
