using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

// Serializes queue snapshots with deletion, worker claims, and result commits.
public sealed class DetectionControl
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Dictionary<(int RunId, int ModelId), CancellationTokenSource> Active { get; } = [];

    public async Task Delete(AppDb db, int? siteId = null, int? keyId = null, int? modelId = null)
    {
        await Gate.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var modelIds = await (from model in db.Models join key in db.Keys on model.SiteKeyId equals key.Id
                where (siteId == null || key.SiteId == siteId) && (keyId == null || key.Id == keyId) && (modelId == null || model.Id == modelId)
                select model.Id).ToArrayAsync();
            var removed = modelIds.ToHashSet();
            var runs = await db.Runs.Where(r => r.Status == "Queued" || r.Status == "Running").ToArrayAsync();
            foreach (var run in runs)
            {
                var targets = JsonSerializer.Deserialize<Target[]>(run.TargetsJson) ?? [];
                var completed = await db.Results.Where(r => r.RunId == run.Id).Select(r => r.ModelId).ToArrayAsync();
                // Preserve finished targets and history; remove only unfinished work.
                var remaining = targets.Where(t => !removed.Contains(t.ModelId) || completed.Contains(t.ModelId)).ToArray();
                if (remaining.Length == targets.Length) continue;
                run.TargetsJson = JsonSerializer.Serialize(remaining);
                run.Total = remaining.Length;
                if (remaining.Length == 0)
                {
                    run.Status = "Cancelled";
                    run.CancelRequested = true;
                    run.CompletedAt = DateTime.UtcNow;
                }
            }
            await db.SaveChangesAsync();
            if (siteId != null) await db.Sites.Where(s => s.Id == siteId).ExecuteDeleteAsync();
            else if (keyId != null) await db.Keys.Where(k => k.Id == keyId).ExecuteDeleteAsync();
            else await db.Models.Where(m => m.Id == modelId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            foreach (var entry in Active.Where(entry => removed.Contains(entry.Key.ModelId)))
                await entry.Value.CancelAsync();
        }
        finally { Gate.Release(); }
    }
}
