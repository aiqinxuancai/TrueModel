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
        // Callers hold DetectionControl.Gate through this check and the insert.
        var pendingRuns = await db.Runs.AsNoTracking().Where(r => r.Status == "Queued" || r.Status == "Running").ToArrayAsync();
        var pendingIds = pendingRuns.Select(r => r.Id).ToArray();
        var completed = (await db.Results.Where(r => pendingIds.Contains(r.RunId))
            .Select(r => new { r.RunId, r.ModelId }).ToArrayAsync()).Select(r => (r.RunId, r.ModelId)).ToHashSet();
        var busyModels = pendingRuns.SelectMany(r => (JsonSerializer.Deserialize<Target[]>(r.TargetsJson) ?? [])
            .Where(t => !completed.Contains((r.Id, t.ModelId))).Select(t => t.ModelId)).ToHashSet();
        targets = targets.Where(t => !busyModels.Contains(t.ModelId)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("所选模型已在检测中或队列中，请勿重复提交。");
        var run = new DetectionRun { Total = targets.Length, BankId = bank.Id, TargetsJson = JsonSerializer.Serialize(targets), Source = source };
        db.Runs.Add(run); await db.SaveChangesAsync(); return run;
    }
}
public sealed class DetectionWorker(IServiceScopeFactory scopes, ModelTraceClient client, IDataProtectionProvider protection, ILogger<DetectionWorker> logger, DetectionControl control) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new List<Task>();
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<AppDb>();
            await db.Runs.Where(r => r.Status == "Running").ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Interrupted").SetProperty(r => r.CompletedAt, DateTime.UtcNow), stoppingToken);
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    workers.RemoveAll(t => t.IsCompleted);
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    var settings = await db.Settings.SingleAsync(stoppingToken);
                    var finished = new List<DetectionRun>();
                    await control.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        if (settings.IntervalMinutes > 0 && settings.NextRunAt <= DateTime.UtcNow)
                        {
                            try { await DetectionJobs.Enqueue(db, new(null, null, null), "Scheduled"); }
                            catch (InvalidOperationException) { }
                            settings.NextRunAt = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        var runs = await db.Runs.Where(r => r.Status == "Queued" || r.Status == "Running").OrderBy(r => r.Id).ToArrayAsync(stoppingToken);
                        foreach (var run in runs)
                        {
                            var active = control.Active.Where(e => e.Key.RunId == run.Id).ToArray();
                            if (run.CancelRequested)
                            {
                                foreach (var entry in active) await entry.Value.CancelAsync();
                            }
                            if (active.Length == 0 && (run.CancelRequested || run.Completed >= run.Total))
                            {
                                run.Status = run.CancelRequested ? "Cancelled" : "Completed";
                                run.CompletedAt = DateTime.UtcNow;
                                finished.Add(run);
                                continue;
                            }
                            if (run.CancelRequested || control.Active.Count >= settings.MaxConcurrency) continue;
                            var completed = (await db.Results.Where(r => r.RunId == run.Id).Select(r => r.ModelId).ToArrayAsync(stoppingToken)).ToHashSet();
                            var targets = JsonSerializer.Deserialize<Target[]>(run.TargetsJson) ?? [];
                            var bank = await db.Banks.SingleAsync(b => b.Id == run.BankId, stoppingToken);
                            foreach (var target in targets)
                            {
                                if (control.Active.Count >= settings.MaxConcurrency) break;
                                if (completed.Contains(target.ModelId) || control.Active.ContainsKey((run.Id, target.ModelId))) continue;
                                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                control.Active.Add((run.Id, target.ModelId), cancellation);
                                run.Status = "Running";
                                // Each detection holds one global slot through its result commit.
                                workers.Add(RunDetection(target, run.Id, bank.Json, settings.ChallengeCount, settings.CandyReasoningEffort, cancellation, stoppingToken));
                            }
                        }
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    finally { control.Gate.Release(); }
                    foreach (var run in finished)
                        workers.Add(NotifyRun(run, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e) { logger.LogError(e, "Detection worker failure"); }
                await Task.Delay(100, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(workers);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDb>().Runs.Where(r => r.Status == "Running")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Interrupted").SetProperty(r => r.CompletedAt, DateTime.UtcNow));
        }
    }

    private async Task NotifyRun(DetectionRun run, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<NotificationService>()
                .NotifyRun(scope.ServiceProvider.GetRequiredService<AppDb>(), run, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("Notification delivery could not be recorded for run {RunId}", run.Id); }
    }

    private async Task RunDetection(Target target, int runId, string bank, int challengeCount, string candyReasoningEffort, CancellationTokenSource cancellation, CancellationToken stoppingToken)
    {
        try
        {
            var result = await Probe(target, runId, bank, challengeCount, candyReasoningEffort, cancellation.Token);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await control.Gate.WaitAsync(stoppingToken);
            try
            {
                if (!await db.Models.AnyAsync(m => m.Id == target.ModelId, stoppingToken)) return;
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                db.Results.Add(result);
                await db.SaveChangesAsync(stoppingToken);
                await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Completed, r => r.Completed + 1), stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            finally { control.Gate.Release(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) { logger.LogError(e, "Detection failed for run {RunId}, model {ModelId}", runId, target.ModelId); }
        finally
        {
            await control.Gate.WaitAsync(CancellationToken.None);
            try { control.Active.Remove((runId, target.ModelId)); }
            finally { control.Gate.Release(); cancellation.Dispose(); }
        }
    }
    private async Task<DetectionResult> Probe(Target target, int runId, string bank, int challengeCount, string candyReasoningEffort, CancellationToken token)
    {
        var result = new DetectionResult { RunId = runId, ModelId = target.ModelId, SiteName = target.SiteName, KeyName = target.KeyName, ModelName = target.ModelName };
        var watch = Stopwatch.StartNew();
        try
        {
            var key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedKey);
            var response = await client.Test(target.BaseUrl, key, target.ModelName, bank, challengeCount, token, candyReasoningEffort);
            result.ResponsesJson = response.GetProperty("responses").GetRawText();
            result.JuiceValue = response.TryGetProperty("juice_value", out var juice) && juice.ValueKind == JsonValueKind.Number ? juice.GetInt32() : null;
            result.JuiceStatus = response.TryGetProperty("juice_status", out var juiceStatus) ? juiceStatus.GetString() : null;
            result.JuicePrompt = response.TryGetProperty("juice_prompt", out var juicePrompt) ? juicePrompt.GetString() : null;
            if (response.TryGetProperty("candy", out var candy))
            {
                var probe = candy.Deserialize<CandyProbeResult>()!;
                result.CandyStatus = probe.CandyStatus;
                result.CandyPrompt = probe.CandyPrompt;
                result.CandyResponse = probe.CandyResponse;
                result.CandyError = probe.CandyError;
                result.CandyReasoningEffort = probe.CandyReasoningEffort;
                result.CandyReasoningTokens = probe.CandyReasoningTokens;
            }
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
