using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

// Manual only; queued tasks survive page closure and retain settings chosen on submission.
public sealed class CraneRidingWorker(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILogger<CraneRidingWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<AppDb>();
            await db.CraneRides.Where(r => r.Status == "Running").ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Interrupted").SetProperty(r => r.Error, "检测因服务重启而中断").SetProperty(r => r.CompletedAt, DateTime.UtcNow), stoppingToken);
            await CraneRidingProbe.Prune(db, stoppingToken);
        }
        var tasks = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                tasks.RemoveAll(t => t.IsCompleted);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var limit = await db.Settings.Select(s => s.MaxConcurrency).SingleAsync(stoppingToken);
                var queued = await db.CraneRides.AsNoTracking().Where(r => r.Status == "Queued").OrderBy(r => r.Id)
                    .Take(Math.Max(0, limit - tasks.Count)).Select(r => r.Id).ToArrayAsync(stoppingToken);
                foreach (var id in queued)
                    if (await db.CraneRides.Where(r => r.Id == id && r.Status == "Queued")
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Running"), stoppingToken) == 1)
                        tasks.Add(Run(id, stoppingToken));
                await Task.Delay(500, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(tasks); }
    }

    private async Task Run(int id, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var run = await db.CraneRides.SingleAsync(r => r.Id == id, CancellationToken.None);
        string? key = null;
        try
        {
            var target = await (from m in db.Models.AsNoTracking() join k in db.Keys on m.SiteKeyId equals k.Id
                join s in db.Sites on k.SiteId equals s.Id where m.Id == run.ModelId
                select new { m.Name, k.ProtectedValue, s.BaseUrl }).SingleOrDefaultAsync(token);
            if (target is null) { run.Status = "Failed"; run.Error = "模型已删除"; }
            else
            {
                key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedValue);
                run.Prompt = CraneRidingProbe.CreatePrompt(PromptLanguages.IsValid(run.Language) ? run.Language : "zh");
                var conversation = run.SimulatedContext ? ConversationEnvelope.CreateSimulatedContext(run.Prompt) : null;
                if (conversation is not null) run.ConversationJson = JsonSerializer.Serialize(conversation);
                await db.SaveChangesAsync(token);
                var watch = Stopwatch.StartNew();
                try
                {
                    run.Response = await scope.ServiceProvider.GetRequiredService<ModelTraceClient>().Completion(target.BaseUrl, key, target.Name, run.Prompt, token,
                        observeReasoningTokens: value => run.ReasoningTokens = value, timeout: RequestTimeout, maxTokens: 32_000, maxAttempts: 2, stream: true, conversation: conversation);
                }
                finally { run.LatencyMs = watch.ElapsedMilliseconds; }
                run.Svg = CraneRidingProbe.ExtractSvg(run.Response);
                run.Status = "Success";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { run.Status = "Interrupted"; run.Error = "检测因服务停止而中断"; }
        catch (Exception e)
        {
            logger.LogWarning("Crane riding probe failed for model {ModelId}: {Type}", run.ModelId, e.GetType().Name);
            run.Status = "Failed";
            run.Error = "丹顶鹤骑行检测失败：" + e.Message;
        }
        string? Redact(string? text) => text is null || string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        run.Response = Redact(run.Response);
        if (run.Response?.Length > CraneRidingProbe.MaxStoredText) run.Response = run.Response[..CraneRidingProbe.MaxStoredText] + "\n…（已截断）";
        run.Svg = Redact(run.Svg);
        run.Error = Redact(run.Error);
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        try { await CraneRidingProbe.Prune(db, CancellationToken.None); }
        catch (DbUpdateException e) { logger.LogWarning("Crane riding pruning failed: {Type}", e.GetType().Name); }
    }
}
