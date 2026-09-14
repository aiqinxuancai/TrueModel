using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed record DetectionScope(int? SiteId, int? KeyId, int? ModelId);
public sealed record Target(int ModelId, string ModelName, string KeyName, string SiteName, string BaseUrl, string ProtectedKey);
public static class DetectionJobs
{
    public static async Task<DetectionRun> Enqueue(AppDb db, DetectionScope scope, string source)
    {
        var bank = await db.Banks.SingleOrDefaultAsync(b => b.Active) ?? throw new InvalidOperationException("No active fingerprint bank.");
        var targets = await (from m in db.Models join k in db.Keys on m.SiteKeyId equals k.Id join s in db.Sites on k.SiteId equals s.Id
            where m.Enabled && k.Enabled && s.Enabled && (scope.SiteId == null || s.Id == scope.SiteId) && (scope.KeyId == null || k.Id == scope.KeyId) && (scope.ModelId == null || m.Id == scope.ModelId)
            select new Target(m.Id, m.Name, k.Name, s.Name, s.BaseUrl, k.ProtectedValue)).ToArrayAsync();
        if (targets.Length == 0) throw new InvalidOperationException("No enabled models in this scope.");
        if (await db.Runs.AnyAsync(r => r.Status == "Queued" || r.Status == "Running")) throw new InvalidOperationException("A detection batch is already active.");
        var run = new DetectionRun { Total = targets.Length, BankId = bank.Id, TargetsJson = JsonSerializer.Serialize(targets), Source = source };
        db.Runs.Add(run); await db.SaveChangesAsync(); return run;
    }
}
public sealed class DetectionWorker(IServiceScopeFactory scopes, ModelTraceClient client, IDataProtectionProvider protection, ILogger<DetectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<AppDb>();
            await db.Runs.Where(r => r.Status == "Running").ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Interrupted").SetProperty(r => r.CompletedAt, DateTime.UtcNow), stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var settings = await db.Settings.SingleAsync(stoppingToken);
                if (settings.IntervalMinutes > 0 && settings.NextRunAt <= DateTime.UtcNow)
                {
                    try { await DetectionJobs.Enqueue(db, new(null, null, null), "Scheduled"); }
                    catch (InvalidOperationException) { }
                    settings.NextRunAt = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
                    await db.SaveChangesAsync(stoppingToken);
                }
                var run = await db.Runs.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Status == "Queued", stoppingToken);
                if (run is not null)
                {
                    run.Status = "Running"; await db.SaveChangesAsync(stoppingToken);
                    var bank = await db.Banks.SingleAsync(b => b.Id == run.BankId, stoppingToken);
                    var targets = JsonSerializer.Deserialize<Target[]>(run.TargetsJson)!;
                    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var monitor = MonitorCancellation(run.Id, cancelled);
                    try
                    {
                        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrency, CancellationToken = cancelled.Token }, async (target, token) =>
                        {
                            var result = await Probe(target, run.Id, bank.Json, settings.ChallengeCount, token);
                            using var resultScope = scopes.CreateScope();
                            var resultDb = resultScope.ServiceProvider.GetRequiredService<AppDb>();
                            resultDb.Results.Add(result); await resultDb.SaveChangesAsync(stoppingToken);
                            await resultDb.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Completed, r => r.Completed + 1), stoppingToken);
                        });
                        run.Status = "Completed";
                    }
                    catch (OperationCanceledException) { run.Status = stoppingToken.IsCancellationRequested ? "Interrupted" : "Cancelled"; }
                    finally { await cancelled.CancelAsync(); await monitor; }
                    run.CompletedAt = DateTime.UtcNow;
                    // Progress is updated atomically by workers, not by this tracked instance.
                    db.Entry(run).Property(r => r.Completed).IsModified = false;
                    await db.SaveChangesAsync(CancellationToken.None);
                    try { await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyRun(db, run, stoppingToken); }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Notification delivery could not be recorded for run {RunId}", run.Id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "Detection worker failure"); }
            try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task MonitorCancellation(int id, CancellationTokenSource cancelled)
    {
        try
        {
            while (!cancelled.IsCancellationRequested)
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<AppDb>().Runs.AnyAsync(r => r.Id == id && r.CancelRequested, cancelled.Token)) { await cancelled.CancelAsync(); return; }
                await Task.Delay(500, cancelled.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task<DetectionResult> Probe(Target target, int runId, string bank, int challengeCount, CancellationToken token)
    {
        var result = new DetectionResult { RunId = runId, ModelId = target.ModelId, SiteName = target.SiteName, KeyName = target.KeyName, ModelName = target.ModelName };
        var watch = Stopwatch.StartNew();
        try
        {
            var key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedKey);
            var response = await client.Test(target.BaseUrl, key, target.ModelName, bank, challengeCount, token);
            result.ResponsesJson = response.GetProperty("responses").GetRawText();
            result.StatusCode = response.TryGetProperty("status_code", out var status) ? status.GetInt32() : 0;
            if (response.TryGetProperty("error", out var error))
            {
                result.Error = error.GetString(); result.Status = "Failed";
                result.LatencyMs = watch.ElapsedMilliseconds; return result;
            }
            var report = response.GetProperty("result");
            var candidates = report.GetProperty("results").EnumerateArray().Select(c => new Candidate(
                c.GetProperty("model").GetString()!, c.GetProperty("display_name").GetString()!,
                c.GetProperty("family").GetString()!, c.GetProperty("probability").GetDouble(), c.GetProperty("score").GetDouble())).ToArray();
            var families = report.GetProperty("family_probabilities").EnumerateArray().ToDictionary(
                c => c.GetProperty("family").GetString()!, c => c.GetProperty("probability").GetDouble());
            result.AttributionJson = JsonSerializer.Serialize(new {
                UsedOutputs = report.GetProperty("used_outputs").GetInt32(),
                Beta = report.GetProperty("calibration").GetProperty("beta").GetDouble(),
                Candidates = candidates, Families = families, ModelTrace = report
            });
            result.Status = "Success";
        }
        catch (OperationCanceledException) { result.Status = token.IsCancellationRequested ? "Cancelled" : "Timeout"; result.Error = result.Status; }
        catch (Exception) { result.Status = "Failed"; result.Error = "检测失败：接口响应或指纹库格式异常。"; }
        result.LatencyMs = watch.ElapsedMilliseconds;

        return result;
    }
}
