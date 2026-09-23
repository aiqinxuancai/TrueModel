using System.Net;
using Microsoft.EntityFrameworkCore;
using TrueModel;

namespace TrueModel.Tests;

public class DefaultBankUpdaterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatesDownloadAndDoesNotOverwriteCurrentBank(bool invalid)
    {
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.Banks.Add(new FingerprintBank { Name = "Custom", Json = "{}", Sha256 = "custom", Active = true });
        await db.SaveChangesAsync();
        var json = invalid ? "{}" : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets/unified_bank.json"));
        using var http = new HttpClient(new Handler(json));
        var updater = new DefaultBankUpdater(http);
        if (invalid)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => updater.Update(db, CancellationToken.None));
            Assert.Equal(1, await db.Banks.CountAsync());
        }
        else
        {
            var first = await updater.Update(db, CancellationToken.None);
            Assert.True(first.Updated);
            Assert.False((await updater.Update(db, CancellationToken.None)).Updated);
            Assert.Equal(2, await db.Banks.CountAsync());
            Attribution.Validate((await db.Banks.SingleAsync(b => b.Id == first.Id)).Json);
        }
        Assert.Equal("Custom", (await db.Banks.SingleAsync(b => b.Active)).Name);
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
