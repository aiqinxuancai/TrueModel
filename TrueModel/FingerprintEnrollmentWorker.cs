using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public static class FingerprintTaskEndpoints
{
    private sealed record Summary(int Id, string ModelName, string SiteName, string KeyName, string TemplateName,
        string FingerprintModel, string DisplayName, string Language, int Total, int Collected, string Status,
        DateTime CreatedAt, DateTime? CompletedAt, string? Error, int? ResultBankId);

    private static readonly Expression<Func<FingerprintTask, Summary>> Project = t => new(t.Id, t.ModelName, t.SiteName,
        t.KeyName, t.TemplateName, t.FingerprintModel, t.DisplayName, t.Language, t.Total, t.Collected, t.Status,
        t.CreatedAt, t.CompletedAt, t.Error, t.ResultBankId);

    public static void MapFingerprintTasks(this RouteGroupBuilder api)
    {
        api.MapPost("/models/{id:int}/fingerprint", Enqueue);
        api.MapGet("/fingerprint-tasks", async (AppDb db, CancellationToken token) =>
        {
            var recent = db.FingerprintTasks.Where(t => t.Status != "Queued" && t.Status != "Running")
                .OrderByDescending(t => t.Id).Take(30).Select(t => t.Id);
            return await db.FingerprintTasks.Where(t => t.Status == "Queued" || t.Status == "Running" || recent.Contains(t.Id))
                .OrderByDescending(t => t.Status == "Running").ThenByDescending(t => t.Status == "Queued")
                .ThenByDescending(t => t.Id).Select(Project).ToArrayAsync(token);
        });
        api.MapGet("/fingerprint-tasks/{id:int}", async (int id, AppDb db, CancellationToken token) =>
            await db.FingerprintTasks.Where(t => t.Id == id).Select(Project).SingleOrDefaultAsync(token) is { } task
                ? Results.Ok(task) : Results.NotFound());
        api.MapPost("/fingerprint-tasks/{id:int}/cancel", async (int id, AppDb db, FingerprintEnrollmentWorker worker, CancellationToken token) =>
        {
            if (await worker.Cancel(db, id, token)) return Results.NoContent();
            var status = await db.FingerprintTasks.Where(t => t.Id == id).Select(t => t.Status).SingleOrDefaultAsync(token);
            return status switch
            {
                null => Results.NotFound(new { error = "采集任务不存在" }),
                "Cancelled" => Results.NoContent(),
                _ => Results.Conflict(new { error = "采集任务已结束" })
            };
        });
    }

    private static async Task<IResult> Enqueue(int id, EnrollmentInput input, AppDb db, CancellationToken token)
    {
        if (input.ChallengeCount is < 3 or > 6 || string.IsNullOrWhiteSpace(input.Model) || input.Model.Length > 200 ||
            string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 200 || string.IsNullOrWhiteSpace(input.Family) || input.Family.Length > 100)
            return Results.BadRequest(new { error = "请填写模型标识、显示名称和家族，采集题数须为 3–6" });
        var template = await db.Banks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == input.BankId, token);
        if (template is null) return Results.NotFound(new { error = "基础指纹库不存在" });
        var target = await (from m in db.Models join k in db.Keys on m.SiteKeyId equals k.Id join s in db.Sites on k.SiteId equals s.Id
            where m.Id == id select new { ModelName = m.Name, KeyName = k.Name, SiteName = s.Name }).SingleOrDefaultAsync(token);
        if (target is null) return Results.NotFound(new { error = "来源模型不存在" });
        var language = await db.Settings.Select(s => s.AttributionLanguage).SingleAsync(token);
        await FingerprintBankSnapshot.Remember(db, [template], token);
        var task = new FingerprintTask
        {
            ModelId = id, ModelName = target.ModelName, KeyName = target.KeyName, SiteName = target.SiteName,
            TemplateName = template.Name, TemplateSha256 = template.Sha256, Language = language,
            FingerprintModel = input.Model.Trim(), DisplayName = input.DisplayName.Trim(), Family = input.Family.Trim(), Total = input.ChallengeCount
        };
        db.FingerprintTasks.Add(task);
        await db.SaveChangesAsync(token);
        return Results.Accepted($"/api/fingerprint-tasks/{task.Id}", new { task.Id, task.Status });
    }
}

public sealed class FingerprintEnrollmentWorker(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILogger<FingerprintEnrollmentWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private (int Id, CancellationTokenSource Cancellation)? active;

    public async Task<bool> Cancel(AppDb db, int id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var updated = await db.FingerprintTasks.Where(t => t.Id == id && (t.Status == "Queued" || t.Status == "Running"))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Cancelled").SetProperty(t => t.CompletedAt, DateTime.UtcNow), token);
            if (updated == 1 && active is { } current && current.Id == id) await current.Cancellation.CancelAsync();
            return updated == 1;
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
            await startup.ServiceProvider.GetRequiredService<AppDb>().FingerprintTasks.Where(t => t.Status == "Running")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Interrupted").SetProperty(t => t.Error, "采集因服务重启而中断")
                    .SetProperty(t => t.CompletedAt, DateTime.UtcNow), stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var task = await Claim(db, cancellation, stoppingToken);
                    if (task is not null)
                    {
                        try { await Run(db, scope.ServiceProvider.GetRequiredService<ModelTraceClient>(), task, cancellation.Token); }
                        finally
                        {
                            await gate.WaitAsync(CancellationToken.None);
                            try { active = null; }
                            finally { gate.Release(); }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e) { logger.LogError("Fingerprint task worker failed: {Type}", e.GetType().Name); }
                await Task.Delay(300, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<FingerprintTask?> Claim(AppDb db, CancellationTokenSource cancellation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var task = await db.FingerprintTasks.AsNoTracking().Where(t => t.Status == "Queued").OrderBy(t => t.Id).FirstOrDefaultAsync(token);
            if (task is null) return null;
            var updated = await db.FingerprintTasks.Where(t => t.Id == task.Id && t.Status == "Queued")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Running"), token);
            if (updated == 0) return null;
            active = (task.Id, cancellation);
            return task;
        }
        finally { gate.Release(); }
    }

    private async Task Run(AppDb db, ModelTraceClient client, FingerprintTask task, CancellationToken token)
    {
        var running = db.FingerprintTasks.Where(t => t.Id == task.Id && t.Status == "Running");
        var status = "Failed";
        string error;
        try
        {
            var target = await (from m in db.Models join k in db.Keys on m.SiteKeyId equals k.Id join s in db.Sites on k.SiteId equals s.Id
                where m.Id == task.ModelId && m.Name == task.ModelName select new { k.ProtectedValue, s.BaseUrl }).SingleOrDefaultAsync(token);
            if (target is null) error = "来源模型已删除或更名";
            else
            {
                var key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedValue);
                var template = await db.FingerprintBankSnapshots.Where(b => b.Sha256 == task.TemplateSha256).Select(b => b.Json).SingleAsync(token);
                var empty = Attribution.CreateEmptyBank(template);
                var outputs = await FingerprintEnrollment.Collect(client, target.BaseUrl, key, task.ModelName, task.Total, token, task.Language,
                    (count, ct) => running.ExecuteUpdateAsync(s => s.SetProperty(t => t.Collected, count), ct));
                token.ThrowIfCancellationRequested();
                var json = Attribution.Enroll(empty, task.FingerprintModel, task.DisplayName, task.Family, outputs);
                var bank = new FingerprintBank
                {
                    Name = $"{task.DisplayName} · 采集 #{task.Id}", Json = json, Active = true,
                    Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
                };
                // Commit the bank only if this worker wins the race with cancellation.
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                db.Banks.Add(bank);
                await db.SaveChangesAsync(token);
                var updated = await running.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Completed")
                    .SetProperty(t => t.ResultBankId, bank.Id).SetProperty(t => t.CompletedAt, DateTime.UtcNow), token);
                if (updated == 1) await transaction.CommitAsync(token);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { status = "Interrupted"; error = "采集因服务停止而中断"; }
        catch (CryptographicException) { error = "无法解密 Key，请重新配置"; }
        catch (Exception e)
        {
            // Some upstream error payloads include credentials.
            logger.LogWarning("Fingerprint task {TaskId} failed: {Type}", task.Id, e.GetType().Name);
            error = "指纹采集失败：接口异常、回答无效或基础库不兼容";
        }
        await running.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status).SetProperty(t => t.Error, error)
            .SetProperty(t => t.CompletedAt, DateTime.UtcNow));
    }
}
