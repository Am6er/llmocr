using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlmOcr;

/// <summary>
/// Manages the lifecycle of the persistent mineru-api server:
/// start (launch process + wait for /health), stop (kill process tree),
/// and health polling for the status lamp. The server process is assigned
/// to a Job Object so it (and its worker children) die when the app exits.
/// </summary>
public sealed class MineruServer : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Action<string> _log;
    private readonly JobObject _job = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private Process? _proc;

    // Access-log noise filtering for the [api] stream.
    private readonly HashSet<string> _loggedTasks = new();
    private static readonly Regex TaskAccessRegex =
        new(@"/tasks/(?<id>[0-9a-fA-F-]{8,})[^""]*""\s+(?<code>\d{3})", RegexOptions.Compiled);
    // tqdm progress bars redraw in place with ANSI escapes (e.g. ESC[A "cursor up");
    // strip them so the plain log doesn't show "[A" tails and blank repaint lines.
    private static readonly Regex AnsiRegex =
        new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    // A tqdm bar frame ends without a newline, so the next loguru line ("2026-… | INFO | …")
    // can arrive glued to the bar's tail ("…84.73it/s]2026-… | INFO | …"). Force a break
    // before every loguru timestamp so the log line lands on its own row.
    private static readonly Regex LoguruStampRegex =
        new(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\s*\|", RegexOptions.Compiled);

    public MineruServer(AppConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>True if we launched the server process and it is still running.</summary>
    public bool Launched => _proc is { HasExited: false };

    /// <summary>PID of the launched mineru-api server (root of the inference process tree),
    /// or null if we didn't launch it. Used by the GPU throttle to suspend/resume the tree.</summary>
    public int? ServerPid => (_proc is { HasExited: false }) ? _proc.Id : null;

    /// <summary>Copies the shared MinerU environment (model mirror, CUDA paths, CPU/RAM
    /// performance knobs — see <see cref="AppConfig.BuildMineruEnv"/>) onto a ProcessStartInfo.
    /// The GPU engines need CUDA_PATH/PATH because the parent may not have inherited them.</summary>
    private void ApplyMineruEnv(System.Collections.Specialized.StringDictionary env)
    {
        foreach (var kv in _cfg.BuildMineruEnv())
            env[kv.Key] = kv.Value;
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(_cfg.HealthUrl);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Logs a line from the mineru-api access stream, filtering out routine noise:
    ///   • /health polling — dropped entirely (server status is shown by the lamp);
    ///   • /tasks/&lt;id&gt; polling — logged once per task_id while the status is OK (2xx/3xx),
    ///     and always logged when the status is NOT OK (≥400).
    /// Everything else passes through unchanged.
    /// </summary>
    private void LogApi(string? data)
    {
        if (data == null) return;

        // tqdm redraws its bar in place: ANSI escapes + carriage returns, no trailing newline,
        // so a bar update and the next real line can arrive glued together. Strip ANSI, then
        // split on CR/LF and emit each piece separately (un-glues bar from following log line).
        data = AnsiRegex.Replace(data, "");
        // Un-glue a loguru line stuck to the end of a tqdm bar frame (no CR/LF between them).
        data = LoguruStampRegex.Replace(data, "\n$0");
        foreach (var raw in data.Split('\r', '\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length > 0) LogApiLine(line);
        }
    }

    private void LogApiLine(string data)
    {
        // Health-check requests are the lamp's job — never surface them.
        if (data.Contains("/health"))
            return;

        var m = TaskAccessRegex.Match(data);
        if (m.Success)
        {
            bool ok = m.Groups["code"].Value.Length == 3 &&
                      (m.Groups["code"].Value[0] == '2' || m.Groups["code"].Value[0] == '3');
            if (ok)
            {
                string id = m.Groups["id"].Value;
                lock (_loggedTasks)
                {
                    if (!_loggedTasks.Add(id))
                        return; // already shown once while OK — suppress repeats
                }
            }
            // NOT OK -> always fall through and log
        }

        _log("[api] " + data);
    }

    /// <summary>
    /// Ensures the server is up. If already healthy (started elsewhere), returns true.
    /// Otherwise launches mineru-api and waits until /health responds or timeout.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (await IsHealthyAsync())
        {
            _log("MinerU уже поднят — использую существующий сервер.");
            return true;
        }

        if (Launched)
        {
            _log("Процесс сервера запущен, жду готовности…");
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cfg.ApiExePath,
                Arguments = $"--host {_cfg.Host} --port {_cfg.Port} {_cfg.ExtraServerArgs}".Trim(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _cfg.MineruDir
            };
            ApplyMineruEnv(psi.EnvironmentVariables);

            _log($"Запуск сервера: \"{psi.FileName}\" {psi.Arguments}");
            _log("  env: " + string.Join("  ", _cfg.BuildMineruEnv()
                     .Where(kv => kv.Key != "PATH")
                     .Select(kv => $"{kv.Key}={kv.Value}")));
            try
            {
                _proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log($"Не удалось запустить mineru-api: {ex.Message}");
                return false;
            }

            if (_proc == null)
            {
                _log("Process.Start вернул null.");
                return false;
            }

            _job.Assign(_proc.Handle);
            _proc.OutputDataReceived += (_, e) => LogApi(e.Data);
            _proc.ErrorDataReceived += (_, e) => LogApi(e.Data);
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        var deadline = DateTime.UtcNow.AddSeconds(_cfg.HealthTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc is { HasExited: true })
            {
                _log($"Сервер неожиданно завершился (код {_proc.ExitCode}).");
                return false;
            }
            if (await IsHealthyAsync())
            {
                _log("Сервер готов (/health OK).");
                return true;
            }
            await Task.Delay(1500, ct);
        }

        _log("Таймаут ожидания готовности сервера.");
        return false;
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _log("Останавливаю сервер (kill tree)…");
                KillTree(_proc.Id);
                _proc.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _log($"Ошибка остановки: {ex.Message}");
        }
        finally
        {
            _proc = null;
        }
    }

    private static void KillTree(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Stop();
        _job.Dispose(); // KILL_ON_JOB_CLOSE — final safety net
        _http.Dispose();
    }
}
