using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueModel;

public sealed class HlwyBaselineUpdater(HttpClient client, string cachePath, ILogger<HlwyBaselineUpdater> logger) : BackgroundService
{
    public const string Source = "https://raw.githubusercontent.com/hanlinwenyuan/hlwy-ai-checker/main/baselines/";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? defaults;

    public JsonElement[] Baselines(string customJson) => HlwyFingerprint.Baselines(
        string.IsNullOrWhiteSpace(customJson) ? Volatile.Read(ref defaults) ?? "" : customJson);

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            var json = File.ReadAllText(cachePath);
            HlwyFingerprint.Baselines(json);
            Volatile.Write(ref defaults, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { logger.LogWarning(e, "无法加载 HLWY 默认基准缓存，使用内置基准"); }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(await client.GetStringAsync(Source + "index.json", token));
            if (names is null || names.Length is < 1 or > 100 || names.Any(n => n is null || !Regex.IsMatch(n, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,199}\z")) || names.Distinct().Count() != names.Length)
                throw new InvalidOperationException("HLWY 基准索引无效。");
            var rows = new List<JsonElement>();
            foreach (var name in names)
            {
                var json = await client.GetStringAsync(Source + name + ".json", token);
                if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("HLWY 基准文件为空。");
                rows.AddRange(HlwyFingerprint.Baselines(json));
            }
            var merged = JsonSerializer.Serialize(rows);
            HlwyFingerprint.Baselines(merged);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, merged, token);
                File.Move(temporary, cachePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref defaults, merged);
            logger.LogInformation("已从 hlwy-ai-checker 同步 {Count} 个默认基准", rows.Count);
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning(e, "HLWY 默认基准同步失败，保留缓存或内置基准"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
