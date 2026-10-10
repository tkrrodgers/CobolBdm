using System.Diagnostics;
using System.Text;

namespace MlCpu.Host.Services.Workspace;

public sealed record ProcessResult(int ExitCode, string Output, TimeSpan Elapsed, bool TimedOut);

/// <summary>
/// The external tools (GnuCOBOL, the JDK, Gradle, dotnet) with the environment the scripts give them: the user-level PATH,
/// GNUCOBOL_HOME and COB_* variables, JAVA_HOME. Processes stream their output line by line and honour cancellation.
/// </summary>
public static class Toolchain
{
    static readonly Encoding Latin1 = Encoding.Latin1;

    static string? User(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(name);

    public static string GnuCobolHome => User("GNUCOBOL_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GnuCOBOL");
    public static string Cobc => User("MLCPU_COBC") ?? Path.Combine(GnuCobolHome, "bin", "cobc.exe");
    public static string JavaHome => User("JAVA_HOME") ?? "";
    public static string Java => Path.Combine(JavaHome, "bin", "java.exe");
    public static string Javac => Path.Combine(JavaHome, "bin", "javac.exe");

    public static string Gradle
    {
        get
        {
            foreach (var dir in PathValue().Split(';', StringSplitOptions.RemoveEmptyEntries))
                foreach (var name in new[] { "gradle.bat", "gradle.cmd", "gradle.exe" })
                    if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
            return "gradle";
        }
    }

    static string PathValue()
    {
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        return $"{GnuCobolHome}\\bin;{GnuCobolHome}\\mingw64\\bin;{(JavaHome.Length > 0 ? JavaHome + "\\bin;" : "")}{user};{machine}";
    }

    /// <summary>The environment every tool runs with (plus <paramref name="extra"/>).</summary>
    public static void Configure(ProcessStartInfo psi, IReadOnlyDictionary<string, string>? extra = null)
    {
        psi.Environment["Path"] = PathValue();
        psi.Environment["GNUCOBOL_HOME"] = GnuCobolHome;
        if (JavaHome.Length > 0) psi.Environment["JAVA_HOME"] = JavaHome;
        if (Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User) is { } user)
            foreach (System.Collections.DictionaryEntry e in user)
                if (e.Key is string k && k.StartsWith("COB_", StringComparison.OrdinalIgnoreCase) && e.Value is string v) psi.Environment[k] = v;
        if (extra is not null) foreach (var (k, v) in extra) psi.Environment[k] = v;
    }

    /// <summary>Runs a process to completion. <paramref name="onLine"/> sees stdout and stderr lines as they arrive.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string workingDir, Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default, string? stdoutFile = null,
        Encoding? encoding = null, bool toolEnvironment = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? (stdoutFile is not null ? Latin1 : Encoding.UTF8), StandardErrorEncoding = encoding ?? Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // git (and other MSYS tools) must not see GnuCOBOL's MinGW DLLs first on PATH.
        if (toolEnvironment) Configure(psi, env);
        else if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        proc.Start();
        Task stdoutTask;
        if (stdoutFile is not null)
        {
            // Byte-exact capture (SYSOUT): copy the raw stream.
            stdoutTask = Task.Run(async () =>
            {
                await using var fs = new FileStream(stdoutFile, FileMode.Append, FileAccess.Write);
                await proc.StandardOutput.BaseStream.CopyToAsync(fs, CancellationToken.None);
            }, CancellationToken.None);
        }
        else
        {
            stdoutTask = Pump(proc.StandardOutput, line => { lock (gate) output.AppendLine(line); onLine?.Invoke(line); });
        }
        var stderrTask = Pump(proc.StandardError, line => { lock (gate) output.AppendLine(line); onLine?.Invoke(line); });
        using var timeoutCts = timeout is null ? new CancellationTokenSource() : new CancellationTokenSource(timeout.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            if (ct.IsCancellationRequested) throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(timedOut ? -1 : proc.ExitCode, output.ToString(), sw.Elapsed, timedOut);
    }

    static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null) onLine(line);
    }

    /// <summary>Copies a directory tree (skipping build output) — robocopy semantics without robocopy.</summary>
    public static void CopyTree(string from, string to, Func<string, bool>? skipDir = null)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            var n = Path.GetFileName(d);
            if (n is "build" or ".gradle" or ".idea" or "bin" or "obj" or "node_modules" || skipDir?.Invoke(n) == true) continue;
            CopyTree(d, Path.Combine(to, n), skipDir);
        }
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    /// <summary>Deletes a directory tree, clearing read-only flags (git objects) first.</summary>
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var a = File.GetAttributes(f);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, true);
    }

    public static string Sha256(params string[] parts)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var p in parts) { h.AppendData(Encoding.UTF8.GetBytes(p)); h.AppendData([0]); }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static string FileSha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }
}
