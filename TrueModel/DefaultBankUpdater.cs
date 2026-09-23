using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed class DefaultBankUpdater(HttpClient client)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<BankUpdateResult> Update(AppDb db, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            using var commits = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/xqy2006/ModelTrace/commits?path=data/unified_bank.json&per_page=1", token));
            var sha = commits.RootElement[0].GetProperty("sha").GetString()!;
            if (sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c))) throw new FormatException("Invalid commit");
            var json = await client.GetStringAsync($"https://raw.githubusercontent.com/xqy2006/ModelTrace/{sha}/data/unified_bank.json", token);
            Attribution.Validate(json);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            var existing = await db.Banks.AsNoTracking().FirstOrDefaultAsync(b => b.Sha256 == hash, token);
            if (existing is not null) return new(existing.Id, false, sha);
            var bank = new FingerprintBank { Name = $"ModelTrace {sha[..7]}", Json = json, Sha256 = hash };
            db.Banks.Add(bank);
            await db.SaveChangesAsync(token);
            return new(bank.Id, true, sha);
        }
        finally { Gate.Release(); }
    }
}
public sealed record BankUpdateResult(int Id, bool Updated, string Commit);
