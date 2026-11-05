using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed record CraneRidingSummary(int Id, int ModelId, string ModelName, string Status, string Language, bool SimulatedContext,
    DateTime CreatedAt, DateTime? CompletedAt, long? LatencyMs, long? ReasoningTokens, string? Error);

public static class CraneRidingEndpoints
{
    private static readonly Expression<Func<CraneRidingRun, CraneRidingSummary>> Summary = r =>
        new(r.Id, r.ModelId, r.ModelName, r.Status, r.Language, r.SimulatedContext, r.CreatedAt, r.CompletedAt, r.LatencyMs, r.ReasoningTokens, r.Error);

    public static void MapCraneRides(this RouteGroupBuilder api)
    {
        api.MapPost("/models/{id:int}/crane-riding", async (int id, AppDb db, CancellationToken token) =>
        {
            var settings = await db.Settings.Select(s => new { s.CraneRidingLanguage, s.CraneRidingSimulatedContext }).SingleAsync(token);
            // Snapshot probe settings and de-duplicate concurrent submissions in one SQLite statement.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO CraneRidingRuns (ModelId, ModelName, Status, Language, SimulatedContext, CreatedAt)
                SELECT Id, Name, 'Queued', {settings.CraneRidingLanguage}, {settings.CraneRidingSimulatedContext}, {DateTime.UtcNow} FROM Models WHERE Id = {id}
                AND NOT EXISTS (SELECT 1 FROM CraneRidingRuns WHERE ModelId = {id} AND Status IN ('Queued', 'Running'))
                """, token);
            if (inserted == 0)
                return await db.Models.AnyAsync(m => m.Id == id, token)
                    ? Results.Conflict(new { error = "丹顶鹤骑行检测已在队列中或运行中，请勿重复提交" }) : Results.NotFound(new { error = "模型不存在" });
            await CraneRidingProbe.Prune(db, token);
            return Results.Accepted("/api/crane-rides/latest", new { status = "Queued" });
        });
        api.MapGet("/crane-rides/latest", async (AppDb db, CancellationToken token) =>
        {
            var groups = await db.CraneRides.AsNoTracking().GroupBy(r => r.ModelId).Select(g => new
            {
                ModelId = g.Key, LatestId = g.Max(r => r.Id), Count = g.Count(),
                LatestSuccessId = g.Where(r => r.Status == "Success").Max(r => (int?)r.Id)
            }).ToArrayAsync(token);
            var ids = groups.Select(g => g.LatestId).ToArray();
            var latest = (await db.CraneRides.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(Summary).ToArrayAsync(token)).ToDictionary(r => r.Id);
            return Results.Ok(groups.Select(g => new { g.ModelId, Latest = latest[g.LatestId], g.LatestSuccessId, g.Count }));
        });
        api.MapGet("/models/{id:int}/crane-rides", async (int id, AppDb db, CancellationToken token) =>
            await db.CraneRides.AsNoTracking().Where(r => r.ModelId == id).OrderByDescending(r => r.Id)
                .Take(CraneRidingProbe.HistoryLimit).Select(Summary).ToArrayAsync(token));
        api.MapGet("/crane-rides/{id:int}", async (int id, AppDb db, CancellationToken token) =>
            await db.CraneRides.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, token) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/crane-rides/{id:int}/image.svg", async (int id, bool? download, AppDb db, HttpContext context, CancellationToken token) =>
        {
            var svg = await db.CraneRides.AsNoTracking().Where(r => r.Id == id && r.Status == "Success").Select(r => r.Svg).SingleOrDefaultAsync(token);
            if (svg is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=86400";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(Encoding.UTF8.GetBytes(svg), "image/svg+xml", download == true ? $"crane-riding-{id}.svg" : null);
        });
    }
}
