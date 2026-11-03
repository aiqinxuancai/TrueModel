using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

// Raw matrices live beside the database; rows and files are pruned together.
public sealed class WorldMapStore(string directory)
{
    public string Directory { get; } = directory;
    public string MatrixPath(int id) => Path.Combine(Directory, $"{id}.csv");
    public string ImagePath(int id) => Path.Combine(Directory, $"{id}.png");

    public async Task Save(int id, byte[] cells, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        await File.WriteAllTextAsync(MatrixPath(id), WorldMapProbe.FormatMatrix(cells), token);
        await File.WriteAllBytesAsync(ImagePath(id), WorldMapProbe.EncodePng(cells), token);
    }

    public async Task<byte[]?> Load(int id, CancellationToken token) =>
        File.Exists(MatrixPath(id)) ? WorldMapProbe.ParseMatrix(await File.ReadAllTextAsync(MatrixPath(id), token)) : null;

    public async Task Prune(AppDb db, CancellationToken token = default)
    {
        var models = await db.Models.Select(m => m.Id).ToArrayAsync(token);
        var rows = await db.WorldMaps.AsNoTracking().Select(w => new { w.Id, w.ModelId, w.Status }).ToArrayAsync(token);
        var expired = rows.Where(w => !models.Contains(w.ModelId) && w.Status is not ("Queued" or "Running"))
            .Concat(rows.GroupBy(w => w.ModelId).SelectMany(g => g.OrderByDescending(w => w.Id).Skip(WorldMapProbe.HistoryLimit)))
            .Select(w => w.Id).Distinct().ToArray();
        foreach (var chunk in expired.Chunk(500)) await db.WorldMaps.Where(w => chunk.Contains(w.Id)).ExecuteDeleteAsync(token);
        if (!System.IO.Directory.Exists(Directory)) return;
        var kept = rows.Select(w => w.Id).Except(expired).ToHashSet();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            var match = Regex.Match(Path.GetFileName(file), @"^(\d+)\.(?:csv|png)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && kept.Contains(id)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

// Manual-only: never enqueued by scheduled or full detection runs.
public sealed class WorldMapWorker(IServiceScopeFactory scopes, IDataProtectionProvider protection, WorldMapStore store, PythonSandbox python, ILogger<WorldMapWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);
    private const int MaxStoredText = 400_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var startup = scopes.CreateScope())
        {
            var db = startup.ServiceProvider.GetRequiredService<AppDb>();
            await db.WorldMaps.Where(w => w.Status == "Running").ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, "Interrupted").SetProperty(w => w.Error, "检测因服务重启而中断").SetProperty(w => w.CompletedAt, DateTime.UtcNow), stoppingToken);
            await store.Prune(db, stoppingToken);
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
                var queued = await db.WorldMaps.AsNoTracking().Where(w => w.Status == "Queued")
                    .OrderBy(w => w.Id).Take(Math.Max(0, limit - tasks.Count)).Select(w => w.Id).ToArrayAsync(stoppingToken);
                foreach (var id in queued)
                    if (await db.WorldMaps.Where(w => w.Id == id && w.Status == "Queued")
                        .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "Running"), stoppingToken) == 1)
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
        var run = await db.WorldMaps.SingleAsync(w => w.Id == id, CancellationToken.None);
        string? key = null;
        try
        {
            await Execute(scope, db, run, value => key = value, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.Status = "Interrupted";
            run.Error = "检测因服务停止而中断";
        }
        catch (Exception e)
        {
            logger.LogWarning("World map probe failed for model {ModelId}: {Type}", run.ModelId, e.GetType().Name);
            run.Status = "Failed";
            run.Error ??= "世界地图检测失败：" + e.Message;
        }
        string? Redact(string? text) => text is null || string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        string? Clip(string? text) => text is null || text.Length <= MaxStoredText ? text : text[..MaxStoredText] + "\n…（已截断）";
        run.Response = Clip(Redact(run.Response));
        run.Script = Clip(Redact(run.Script));
        run.Output = Redact(run.Output);
        run.Error = Redact(run.Error);
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        try { await store.Prune(db, CancellationToken.None); }
        catch (Exception e) when (e is IOException or DbUpdateException) { logger.LogWarning("World map pruning failed: {Message}", e.Message); }
    }

    private async Task Execute(IServiceScope scope, AppDb db, WorldMapRun run, Action<string> observeKey, CancellationToken token)
    {
        var target = await (from m in db.Models.AsNoTracking() join k in db.Keys on m.SiteKeyId equals k.Id
            join s in db.Sites on k.SiteId equals s.Id where m.Id == run.ModelId
            select new { m.Name, k.ProtectedValue, s.BaseUrl }).SingleOrDefaultAsync(token);
        if (target is null) { run.Status = "Failed"; run.Error = "模型已删除"; return; }
        // Check the interpreter first so a missing runtime never costs a long model request.
        var interpreter = await python.Find(token);
        if (interpreter is null)
        {
            run.Status = "Failed";
            run.Error = "未检测到可用的 Python 3 运行环境，无法执行模型生成的脚本。Docker 镜像已内置 Python；其他环境请安装 Python 3（建议同时安装 numpy 和 Pillow）并加入 PATH，或通过配置项 WorldMap__Python 指定解释器路径。";
            return;
        }
        var key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedValue);
        observeKey(key);
        run.Prompt = WorldMapProbe.CreatePrompt(PromptLanguages.IsValid(run.Language) ? run.Language : "zh");
        await db.SaveChangesAsync(token);
        var watch = Stopwatch.StartNew();
        long? reasoningTokens = null;
        try
        {
            run.Response = await scope.ServiceProvider.GetRequiredService<ModelTraceClient>().Completion(target.BaseUrl, key, target.Name, run.Prompt, token,
                observeReasoningTokens: value => reasoningTokens = value, timeout: RequestTimeout, maxTokens: 32_000, maxAttempts: 2);
        }
        catch (Exception e) when (e is UpstreamException or HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            run.Status = "Failed";
            run.Error = "模型请求失败：" + e.Message;
            return;
        }
        finally
        {
            run.LatencyMs = watch.ElapsedMilliseconds;
            run.ReasoningTokens = reasoningTokens;
        }
        run.Script = WorldMapProbe.ExtractScript(run.Response);
        if (run.Script is null)
        {
            run.Status = "Failed";
            run.Error = "回复中未找到 Python 代码块，模型可能拒答或只给出了说明文字";
            return;
        }
        var directory = python.CreateWorkDirectory();
        try
        {
            var result = await python.Run(interpreter, run.Script, directory, token);
            run.Output = result.Output;
            var (cells, problem) = FindMatrix(directory);
            if (cells is null)
            {
                run.Status = "Failed";
                run.Error = (result.TimedOut ? $"脚本运行超过 {PythonSandbox.ScriptTimeout.TotalSeconds:0} 秒被终止。" : result.ExitCode != 0 ? $"脚本退出码 {result.ExitCode}。" : "") + problem;
                return;
            }
            await store.Save(run.Id, cells, token);
            run.LandCount = cells.Count(c => c == 1);
            run.Status = "Success";
            // The matrix is what we score; a later crash (e.g. missing Pillow) is only a warning.
            if (result.ExitCode != 0) run.Error = $"矩阵已生成，但脚本退出码为 {result.ExitCode}（可能缺少 numpy/Pillow 等依赖，图片由服务端根据矩阵生成）";
        }
        finally
        {
            try { System.IO.Directory.Delete(directory, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning("Could not remove {Directory}", directory); }
        }
    }

    private static (byte[]? Cells, string Problem) FindMatrix(string directory)
    {
        var preferred = Path.Combine(directory, WorldMapProbe.MatrixFile);
        var files = (File.Exists(preferred) ? new[] { preferred } : [])
            .Concat(System.IO.Directory.EnumerateFiles(directory, "*.csv", SearchOption.AllDirectories).Where(f => f != preferred).Take(20)).ToArray();
        if (files.Length == 0) return (null, $"脚本未生成 CSV 矩阵（期望文件 {WorldMapProbe.MatrixFile}）");
        var problems = new List<string>();
        foreach (var file in files)
        {
            if (new FileInfo(file).Length > 2_000_000) { problems.Add($"{Path.GetFileName(file)}：文件过大"); continue; }
            try { return (WorldMapProbe.ParseMatrix(File.ReadAllText(file)), ""); }
            catch (FormatException e) { problems.Add($"{Path.GetRelativePath(directory, file)}：{e.Message}"); }
        }
        return (null, "CSV 矩阵不合规：" + string.Join("；", problems));
    }
}
