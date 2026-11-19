using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrueModel;

namespace TrueModel.Tests;

public class DefaultBankUpgradeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupDoesNotUpgradeExistingBanks(bool oldIsActive)
    {
        const string oldHash = "B25D306B1A40FE489469C0FFF6B11C74A87A1F12BC6A461270201EED884034EF";
        const string newHash = "6A678E6C73EB015C1C507D61CDD1313B41916D55186BB65920D1BF119EC5009E";
        var directory = Path.Combine(Path.GetTempPath(), "truemodel-upgrade-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")}",
                ["Admin:Password"] = "test-password-123!", ["Desktop:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            })));
        int oldId;
        int activeId;
        string originalJson;
        await using (var factory = CreateFactory())
        {
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var bank = await db.Banks.SingleAsync();
            oldId = bank.Id;
            originalJson = bank.Json;
            // Simulate the persisted identity and selection of the previous default.
            bank.Name = "ModelTrace 60949ef";
            bank.Sha256 = oldHash;
            bank.Active = oldIsActive;
            db.Banks.Add(new FingerprintBank { Name = "Custom", Json = bank.Json, Sha256 = "custom", Active = !oldIsActive });
            await db.SaveChangesAsync();
            activeId = await db.Banks.Where(b => b.Active).Select(b => b.Id).SingleAsync();
        }
        await using (var legacy = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}"))
        {
            await legacy.OpenAsync();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                DROP TABLE FingerprintTasks;
                DROP TABLE FingerprintBankSnapshots;
                ALTER TABLE Runs DROP COLUMN BankSelectionJson;
                INSERT INTO Runs (StartedAt, Status, Source, Total, Completed, BankId, CancelRequested, TargetsJson)
                VALUES (datetime('now'), 'Queued', 'Manual', 1, 0, $bank, 0, '[]');
                """;
            command.Parameters.AddWithValue("$bank", activeId);
            await command.ExecuteNonQueryAsync();
        }
        for (var restart = 0; restart < 2; restart++)
        {
            await using var factory = CreateFactory();
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            Assert.Equal(2, await db.Banks.CountAsync());
            var old = await db.Banks.SingleAsync(b => b.Id == oldId);
            Assert.Equal(originalJson, old.Json);
            Assert.Equal(oldIsActive, old.Active);
            Assert.False(await db.Banks.AnyAsync(b => b.Sha256 == newHash));
            Assert.Equal(!oldIsActive, (await db.Banks.SingleAsync(b => b.Name == "Custom")).Active);
            var run = await db.Runs.SingleAsync();
            var snapshot = await db.FingerprintBankSnapshots.SingleAsync();
            var reference = Assert.Single(JsonSerializer.Deserialize<FingerprintBankReference[]>(run.BankSelectionJson!)!);
            Assert.Equal(activeId, reference.Id);
            Assert.Equal(oldIsActive ? oldHash : "custom", snapshot.Sha256);
            Assert.Equal(snapshot.Sha256, reference.Sha256);
            Assert.Equal(originalJson, snapshot.Json);
            Assert.Empty(await db.FingerprintTasks.ToArrayAsync());
        }
    }
}
