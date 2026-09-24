using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

// Persist claims before starting requests; browser lifetimes never own this work.
public sealed class CandyWorker(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILogger<CandyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<AppDb>();
            await db.Models.Where(m => m.CandyRefreshStatus == "Running").ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CandyRefreshStatus, "Interrupted"), stoppingToken);
        }
        var tasks = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                tasks.RemoveAll(t => t.IsCompleted);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var limit = (await db.Settings.AsNoTracking().SingleAsync(stoppingToken)).MaxConcurrency;
                var queued = await db.Models.AsNoTracking().Where(m => m.CandyRefreshStatus == "Queued")
                    .OrderBy(m => m.Id).Take(Math.Max(0, limit - tasks.Count)).Select(m => m.Id).ToArrayAsync(stoppingToken);
                foreach (var id in queued)
                {
                    if (await db.Models.Where(m => m.Id == id && m.CandyRefreshStatus == "Queued")
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.CandyRefreshStatus, "Running"), stoppingToken) == 1)
                        tasks.Add(Run(id, stoppingToken));
                }
                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(tasks); }
    }

    private async Task Run(int id, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        try
        {
            var target = await (from m in db.Models.AsNoTracking() join k in db.Keys on m.SiteKeyId equals k.Id
                join s in db.Sites on k.SiteId equals s.Id where m.Id == id
                select new { m.Name, m.CandyRequestedEffort, k.ProtectedValue, s.BaseUrl }).SingleOrDefaultAsync(token);
            if (target is null) return;
            var key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedValue);
            var result = await scope.ServiceProvider.GetRequiredService<ModelTraceClient>()
                .ProbeCandy(target.BaseUrl, key, target.Name, token, target.CandyRequestedEffort ?? "default");
            await db.Models.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CandyStatus, result.CandyStatus)
                .SetProperty(m => m.CandyPrompt, result.CandyPrompt)
                .SetProperty(m => m.CandyResponse, result.CandyResponse)
                .SetProperty(m => m.CandyError, result.CandyError)
                .SetProperty(m => m.CandyReasoningEffort, result.CandyReasoningEffort)
                .SetProperty(m => m.CandyReasoningTokens, result.CandyReasoningTokens)
                .SetProperty(m => m.CandyCheckedAt, DateTime.UtcNow)
                .SetProperty(m => m.CandyRefreshStatus, "Completed"), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await db.Models.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.CandyRefreshStatus, "Interrupted"));
        }
        catch (Exception)
        {
            logger.LogWarning("Candy refresh failed for model {ModelId}", id);
            await db.Models.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CandyRefreshStatus, "Failed")
                .SetProperty(m => m.CandyStatus, "Failed")
                .SetProperty(m => m.CandyPrompt, CandyProbe.Prompt)
                .SetProperty(m => m.CandyResponse, (string?)null)
                .SetProperty(m => m.CandyError, "糖果后台检测失败，请检查模型配置后重试")
                .SetProperty(m => m.CandyReasoningEffort, m => m.CandyRequestedEffort)
                .SetProperty(m => m.CandyReasoningTokens, (long?)null)
                .SetProperty(m => m.CandyCheckedAt, DateTime.UtcNow));
        }
    }
}
