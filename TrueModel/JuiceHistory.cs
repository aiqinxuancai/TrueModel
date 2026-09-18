using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed class JuiceHistory(IServiceScopeFactory scopes)
{
    public async Task<JuiceMethod[]> Ranked(string endpoint, string model, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var statistics = await db.JuiceMethodStatistics.AsNoTracking()
            .Where(s => s.Endpoint == endpoint && s.Model == model).ToArrayAsync(token);
        return JuiceProbe.Rank(statistics);
    }

    public async Task Record(string endpoint, string model, string method, bool success, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        // Atomic increments preserve counts when multiple keys probe the same model.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO JuiceMethodStatistics (Endpoint, Model, Method, Attempts, Successes)
            VALUES ({endpoint}, {model}, {method}, 1, { (success ? 1 : 0) })
            ON CONFLICT (Endpoint, Model, Method) DO UPDATE SET
                Attempts = Attempts + 1, Successes = Successes + excluded.Successes
            """, token);
    }
}

public sealed class JuiceMethodStatistic
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Method { get; set; } = "";
    public long Attempts { get; set; }
    public long Successes { get; set; }
}
