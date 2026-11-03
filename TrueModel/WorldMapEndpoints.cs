using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed record WorldMapSummary(int Id, int ModelId, string ModelName, string Status, string Language, DateTime CreatedAt, DateTime? CompletedAt,
    long? LatencyMs, long? ReasoningTokens, int? LandCount, string? Error);

public static class WorldMapEndpoints
{
    private static readonly Expression<Func<WorldMapRun, WorldMapSummary>> Summary = w =>
        new(w.Id, w.ModelId, w.ModelName, w.Status, w.Language, w.CreatedAt, w.CompletedAt, w.LatencyMs, w.ReasoningTokens, w.LandCount, w.Error);

    public static void MapWorldMaps(this RouteGroupBuilder api)
    {
        api.MapPost("/models/{id:int}/world-map", async (int id, AppDb db, WorldMapStore store, CancellationToken token) =>
        {
            var language = await db.Settings.Select(s => s.WorldMapLanguage).SingleAsync(token);
            // A single statement keeps concurrent clicks from queueing the same model twice.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO WorldMapRuns (ModelId, ModelName, Status, Language, CreatedAt)
                SELECT Id, Name, 'Queued', {language}, {DateTime.UtcNow} FROM Models WHERE Id = {id}
                AND NOT EXISTS (SELECT 1 FROM WorldMapRuns WHERE ModelId = {id} AND Status IN ('Queued', 'Running'))
                """, token);
            if (inserted == 0)
                return await db.Models.AnyAsync(m => m.Id == id, token)
                    ? Results.Conflict(new { error = "世界地图检测已在队列中或运行中，请勿重复提交" }) : Results.NotFound(new { error = "模型不存在" });
            await store.Prune(db, token);
            return Results.Accepted("/api/world-maps/latest", new { status = "Queued" });
        });
        api.MapGet("/world-maps/latest", async (AppDb db, CancellationToken token) =>
        {
            var groups = await db.WorldMaps.AsNoTracking().GroupBy(w => w.ModelId).Select(g => new
            {
                ModelId = g.Key, LatestId = g.Max(w => w.Id), Count = g.Count(),
                LatestSuccessId = g.Where(w => w.Status == "Success").Max(w => (int?)w.Id)
            }).ToArrayAsync(token);
            var ids = groups.Select(g => g.LatestId).ToArray();
            var latest = (await db.WorldMaps.AsNoTracking().Where(w => ids.Contains(w.Id)).Select(Summary).ToArrayAsync(token)).ToDictionary(w => w.Id);
            return Results.Ok(groups.Select(g => new { g.ModelId, Latest = latest[g.LatestId], g.LatestSuccessId, g.Count }));
        });
        api.MapGet("/models/{id:int}/world-maps", async (int id, AppDb db, CancellationToken token) =>
            await db.WorldMaps.AsNoTracking().Where(w => w.ModelId == id).OrderByDescending(w => w.Id)
                .Take(WorldMapProbe.HistoryLimit).Select(Summary).ToArrayAsync(token));
        api.MapGet("/world-maps/{id:int}", async (int id, AppDb db, CancellationToken token) =>
            await db.WorldMaps.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, token) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/world-maps/{id:int}/image", async (int id, int? scale, WorldMapStore store, HttpContext context, CancellationToken token) =>
        {
            if (scale is not (null or 1 or WorldMapProbe.Scale)) return Results.BadRequest(new { error = "仅支持原始尺寸或 8 倍最近邻放大" });
            byte[]? cells;
            try { cells = await store.Load(id, token); }
            catch (FormatException) { return Results.NotFound(); }
            if (cells is null) return Results.NotFound();
            // Results are immutable per id, so thumbnails survive the 5-second polling re-render.
            context.Response.Headers.CacheControl = "private, max-age=86400";
            var size = scale ?? 1;
            return Results.File(WorldMapProbe.EncodePng(cells, size), "image/png", $"world-map-{id}-{WorldMapProbe.Columns * size}x{WorldMapProbe.Rows * size}.png");
        });
        api.MapGet("/world-maps/{id:int}/matrix.csv", (int id, WorldMapStore store) =>
            File.Exists(store.MatrixPath(id)) ? Results.File(store.MatrixPath(id), "text/csv", $"world-map-{id}.csv") : Results.NotFound());
        api.MapGet("/world-maps/{id:int}/script.py", async (int id, AppDb db, CancellationToken token) =>
            await db.WorldMaps.AsNoTracking().Where(w => w.Id == id).Select(w => w.Script).SingleOrDefaultAsync(token) is { } script
                ? Results.File(Encoding.UTF8.GetBytes(script), "text/x-python", $"world-map-{id}.py") : Results.NotFound());
    }
}
