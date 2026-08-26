using System.Diagnostics;
using System.Text.Json;

namespace WiiCompiled.SimpleUI;

/// <summary>
/// Terminal outcome of one --progress-json invocation of WiiCompiled-Setup.exe. Success reflects
/// the "result" line's own success flag (did the operation itself complete without error) - for
/// --check-products that is true even when a rebuild is required, so callers that care about that
/// distinction must look at ExitCode (2 = rebuild required) rather than Success alone.
/// </summary>
internal sealed record SetupResult(bool Success, string? Error, string? InstallDirectory, int ExitCode);

/// <summary>
/// Drives WiiCompiled-Setup.exe exactly the way a frontend is meant to: as a child process reading
/// its <c>--progress-json</c> protocol (see InstallProgress.cs / docs/WHEELWIZARD_CONTRACT.md in the
/// setup project) - one JSON object per stdout line, diagnostics on stderr, a single terminal
/// "result" line. This is the whole integration surface; nothing here reaches into the setup
/// project's internals.
/// </summary>
internal sealed class SetupClient
{
    private readonly string _setupExePath;

    public SetupClient(string setupExePath) => _setupExePath = setupExePath;

    /// <summary>For modes that speak the --progress-json protocol: --silent, --check-products,
    /// --repair-products. Never valid for --launch-base/--launch-retro/--version, which reject the
    /// flag outright (see CommandLine.ModeRules).</summary>
    public async Task<SetupResult> RunAsync(string[] arguments,
        Action<string, string, int> onProgress, Action<string> onDiagnostic,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = _setupExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_setupExePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--progress-json");

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        SetupResult? result = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var parsed = TryParseLine(e.Data);
            if (parsed is null) { onDiagnostic(e.Data); return; }
            switch (parsed.Value.Type)
            {
                case "progress":
                    onProgress(parsed.Value.Stage ?? "", parsed.Value.Message ?? "", parsed.Value.Percent ?? 0);
                    break;
                case "result":
                    result = new SetupResult(parsed.Value.Success ?? false, parsed.Value.Error,
                        parsed.Value.InstallDir, ExitCode: 0);
                    break;
                default:
                    // "products" and anything future-versioned: not this client's concern here.
                    onDiagnostic(e.Data);
                    break;
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onDiagnostic(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return result is null
            ? new SetupResult(process.ExitCode == 0, process.ExitCode == 0
                ? null
                : $"WiiCompiled-Setup.exe exited with code {process.ExitCode} without reporting a result.",
                null, process.ExitCode)
            : result with { ExitCode = process.ExitCode };
    }

    /// <summary>For --launch-base/--launch-retro/--version: no --progress-json (those modes reject
    /// it), blocks for the whole run - launch waits for the game window to close, exactly like
    /// starting it from Explorer would.</summary>
    public async Task<int> RunSimpleAsync(string[] arguments, Action<string> onDiagnostic,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = _setupExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_setupExePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onDiagnostic(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onDiagnostic(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private readonly record struct ProgressLine(string Type, string? Stage, string? Message, int? Percent,
        bool? Success, string? Error, string? InstallDir);

    private static ProgressLine? TryParseLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return null;
            return new ProgressLine(
                typeElement.GetString() ?? "",
                root.TryGetProperty("stage", out var s) ? s.GetString() : null,
                root.TryGetProperty("message", out var m) ? m.GetString() : null,
                root.TryGetProperty("percent", out var p) ? p.GetInt32() : null,
                root.TryGetProperty("success", out var su) ? su.GetBoolean() : null,
                root.TryGetProperty("error", out var er) ? er.GetString() : null,
                root.TryGetProperty("installDir", out var id) ? id.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
