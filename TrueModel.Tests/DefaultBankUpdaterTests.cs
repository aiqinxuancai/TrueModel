using System.Net;
using Microsoft.EntityFrameworkCore;
using TrueModel;

namespace TrueModel.Tests;

public class DefaultBankUpdaterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdatesDefaultInPlaceAndPreservesSelection(bool invalid, bool active)
    {
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.Banks.Add(new FingerprintBank { Name = "Custom", Json = "{}", Sha256 = "custom", Active = !active });
        var original = new FingerprintBank { Name = "Old default", Json = "{}", Sha256 = "old", IsDefault = true, Active = active };
        db.Banks.Add(original);
        await db.SaveChangesAsync();
        var json = invalid ? "{}" : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        using var http = new HttpClient(new Handler(json));
        var updater = new DefaultBankUpdater(http);
        if (invalid)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => updater.Update(db, CancellationToken.None));
            Assert.Equal("old", original.Sha256);
            Assert.Equal("{}", original.Json);
        }
        else
        {
            var first = await updater.Update(db, CancellationToken.None);
            Assert.True(first.Updated);
            Assert.Equal(original.Id, first.Id);
            Assert.False((await updater.Update(db, CancellationToken.None)).Updated);
            Assert.Equal(2, await db.Banks.CountAsync());
            Attribution.Validate((await db.Banks.SingleAsync(b => b.Id == first.Id)).Json);
        }
        Assert.Equal(2, await db.Banks.CountAsync());
        Assert.Equal(active, original.Active);
        Assert.Equal("{}", (await db.Banks.SingleAsync(b => b.Name == "Custom")).Json);
        Assert.Equal(1, await db.Banks.CountAsync(b => b.Active));
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var sha = new string('a', 40);
            var isCommit = request.RequestUri!.Host == "api.github.com";
            if (!isCommit) Assert.Equal($"https://raw.githubusercontent.com/xqy2006/ModelTrace/{sha}/data/unified_bank.json", request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isCommit ? $"[{{\"sha\":\"{sha}\"}}]" : json)
            });
        }
    }
}
