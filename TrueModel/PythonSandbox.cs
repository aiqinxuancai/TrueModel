using System.Diagnostics;
using System.Text;

namespace TrueModel;

public sealed record PythonResult(int ExitCode, bool TimedOut, string Output);

// Runs model-written scripts. This is containment, not a security boundary:
// a clean environment, a throwaway directory, a time limit and, when running
// as root on Linux (Docker), an unprivileged uid so /data stays unreadable.
public sealed class PythonSandbox(IConfiguration configuration, ILogger<PythonSandbox> logger)
{
    public static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(180);
    private const int OutputLimit = 16_000;

    public async Task<string[]?> Find(CancellationToken token)
    {
        var configured = configuration["WorldMap:Python"];
        IEnumerable<string[]> candidates = !string.IsNullOrWhiteSpace(configured) ? [[configured.Trim()]]
            : OperatingSystem.IsWindows() ? [["python"], ["py", "-3"], ["python3"]] : [["python3"], ["python"]];
        foreach (var candidate in candidates)
        {
            try
            {
                var result = await Execute(candidate, ["--version"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(10), token);
                // The Windows Store alias exits non-zero without printing a version.
                if (result.ExitCode == 0 && result.Output.TrimStart().StartsWith("Python 3", StringComparison.Ordinal)) return candidate;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return null;
    }

    public string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "truemodel-worldmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        // The unprivileged uid must be able to write its outputs here.
        if (Dropped && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0b111_111_111);
        return path;
    }

    public Task<PythonResult> Run(string[] python, string script, string directory, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(directory, "world_map.py"), script, new UTF8Encoding(false));
        return Execute(python, ["-I", "world_map.py"], directory, ScriptTimeout, token, sandbox: true);
    }

    // Lock down the data directory so a dropped-privilege script cannot read keys or the database.
    public static void ProtectDataDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static readonly bool Dropped = !OperatingSystem.IsWindows() && Environment.UserName == "root" && File.Exists("/usr/bin/setpriv");

    private async Task<PythonResult> Execute(string[] command, string[] arguments, string directory, TimeSpan timeout, CancellationToken token, bool sandbox = false)
    {
        var start = new ProcessStartInfo { WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var prefix = sandbox && Dropped ? new[] { "/usr/bin/setpriv", "--reuid=65534", "--regid=65534", "--clear-groups", "--" } : [];
        var all = prefix.Concat(command).Concat(arguments).ToArray();
        start.FileName = all[0];
        foreach (var argument in all.Skip(1)) start.ArgumentList.Add(argument);
        if (sandbox)
        {
            // Never leak admin passwords, API keys or proxy credentials to generated code.
            var keep = new[] { "PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "LANG", "LC_ALL", "TZ" };
            var preserved = keep.Select(name => (name, value: Environment.GetEnvironmentVariable(name))).Where(v => v.value is not null).ToArray();
            start.Environment.Clear();
            foreach (var (name, value) in preserved) start.Environment[name] = value;
            foreach (var name in new[] { "HOME", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "MPLCONFIGDIR" }) start.Environment[name] = directory;
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            start.Environment["MPLBACKEND"] = "Agg";
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Python 进程");
        process.StandardInput.Close();
        var output = new StringBuilder();
        var reading = Task.WhenAll(Drain(process.StandardOutput, output), Drain(process.StandardError, output));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !token.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        try { await reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (TimeoutException) { logger.LogWarning("Python output streams did not close after exit"); }
        string text;
        lock (output) text = output.ToString();
        return new(timedOut ? -1 : process.ExitCode, timedOut, text);
    }

    private static async Task Drain(StreamReader reader, StringBuilder output)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            lock (output)
                if (output.Length < OutputLimit) output.Append(buffer, 0, Math.Min(read, OutputLimit - output.Length));
    }
}
