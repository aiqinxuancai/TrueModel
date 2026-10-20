using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed record ModelSelectionInput(int[]? Ids);
public sealed record ModelReplacementInput(int[]? Ids, string? Find, string? Replacement, bool MatchWholeId = false);
public sealed record ModelFavoriteInput(bool IsFavorite);

public static class ModelManagement
{
    private static bool ValidIds(int[]? ids) => ids is { Length: >= 1 and <= 500 } && ids.All(id => id > 0);

    public static void MapModelManagement(this RouteGroupBuilder api)
    {
        api.MapPut("/models/{id:int}/favorite", async (int id, ModelFavoriteInput input, AppDb db, CancellationToken token) =>
        {
            var updated = await db.Models.Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsFavorite, input.IsFavorite), token);
            return updated == 0 ? Results.NotFound(new { error = "模型不存在" }) : Results.NoContent();
        });
        api.MapPost("/models/batch-delete", async (ModelSelectionInput input, AppDb db, DetectionControl control) =>
        {
            if (!ValidIds(input.Ids)) return Results.BadRequest(new { error = "请选择 1–500 个模型" });
            var ids = input.Ids!.Distinct().ToArray();
            var deleted = await control.Delete(db, selectedIds: ids);
            return Results.Ok(new { deleted, skipped = ids.Length - deleted });
        });
        api.MapPost("/models/batch-replace", Replace);
    }

    private static async Task<IResult> Replace(ModelReplacementInput input, AppDb db, DetectionControl control, CancellationToken token)
    {
        if (!ValidIds(input.Ids)) return Results.BadRequest(new { error = "请选择 1–500 个模型" });
        if (string.IsNullOrEmpty(input.Find) || input.Replacement is null)
            return Results.BadRequest(new { error = "请填写查找内容；替换内容可以留空" });
        await control.Gate.WaitAsync(token);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var ids = input.Ids!.Distinct().ToArray();
            var models = await db.Models.Where(m => ids.Contains(m.Id)).ToArrayAsync(token);
            if (models.Length != ids.Length) return Results.NotFound(new { error = "部分模型已删除，请刷新后重新选择" });
            var changes = models.Select(m => (Model: m, Name: (input.MatchWholeId
                ? m.Name == input.Find ? input.Replacement : m.Name
                : m.Name.Replace(input.Find, input.Replacement, StringComparison.Ordinal)).Trim()))
                .Where(change => change.Name != change.Model.Name).ToArray();
            if (changes.Any(change => string.IsNullOrWhiteSpace(change.Name)))
                return Results.BadRequest(new { error = "替换后的模型 ID 不能为空" });
            if (changes.Length == 0) return Results.Ok(new { replaced = 0, skipped = models.Length });

            var changedIds = changes.Select(change => change.Model.Id).ToHashSet();
            var runs = await db.Runs.AsNoTracking().Where(r => r.Status == "Queued" || r.Status == "Running").ToArrayAsync(token);
            var runIds = runs.Select(r => r.Id).ToArray();
            var completed = (await db.Results.Where(r => runIds.Contains(r.RunId))
                .Select(r => new { r.RunId, r.ModelId }).ToArrayAsync(token)).Select(r => (r.RunId, r.ModelId)).ToHashSet();
            if (control.Active.Keys.Any(entry => changedIds.Contains(entry.ModelId)) ||
                changes.Any(change => change.Model.CandyRefreshStatus is "Queued" or "Running") ||
                runs.Any(run => (JsonSerializer.Deserialize<Target[]>(run.TargetsJson) ?? [])
                    .Any(target => changedIds.Contains(target.ModelId) && !completed.Contains((run.Id, target.ModelId)))))
                return Results.Conflict(new { error = "部分模型正在检测或排队，请等待完成或取消检测后再替换" });

            var keyIds = changes.Select(change => change.Model.SiteKeyId).Distinct().ToArray();
            var existing = await db.Models.AsNoTracking().Where(m => keyIds.Contains(m.SiteKeyId)).ToArrayAsync(token);
            var finalNames = existing.Where(m => !changedIds.Contains(m.Id)).Select(m => (m.SiteKeyId, m.Name))
                .Concat(changes.Select(change => (change.Model.SiteKeyId, change.Name))).ToArray();
            if (finalNames.Distinct().Count() != finalNames.Length)
                return Results.Conflict(new { error = "替换后同一 Key 下存在重复模型 ID，请调整替换内容或选择范围" });

            // Intermediate names allow chained renames without violating the unique index.
            var prefix = $"__truemodel_{Guid.NewGuid():N}_";
            foreach (var change in changes) change.Model.Name = prefix + change.Model.Id;
            await db.SaveChangesAsync(token);
            foreach (var change in changes)
            {
                var model = change.Model;
                model.Name = change.Name;
                // Results still describe the old ID in history, never the newly configured ID.
                model.JuiceValue = null; model.JuiceStatus = null; model.JuicePrompt = null; model.JuiceCheckedAt = null;
                model.CandyStatus = null; model.CandyPrompt = null; model.CandyResponse = null; model.CandyError = null;
                model.CandyReasoningEffort = null; model.CandyReasoningTokens = null; model.CandyLatencyMs = null; model.CandyCheckedAt = null;
                model.CandyRefreshStatus = null; model.CandyRequestedEffort = null;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok(new { replaced = changes.Length, skipped = models.Length - changes.Length });
        }
        finally { control.Gate.Release(); }
    }
}
