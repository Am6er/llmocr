using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmOcr;

/// <summary>Reads the NVIDIA GPU temperature via nvidia-smi.</summary>
public sealed class GpuMonitor
{
    private readonly string _exe;

    public GpuMonitor(string nvidiaSmiPath)
    {
        _exe = string.IsNullOrWhiteSpace(nvidiaSmiPath) ? "nvidia-smi" : nvidiaSmiPath;
    }

    /// <summary>Current GPU temperature in °C, or null if nvidia-smi is unavailable.</summary>
    public async Task<int?> ReadTempAsync(CancellationToken ct = default)
        => (await ReadTempUtilAsync(ct)).temp;

    /// <summary>Current GPU temperature (°C) and utilization (%), either null if unavailable.
    /// Utilization lets the throttle avoid pausing during CPU-bound phases (e.g. PDF render).</summary>
    public async Task<(int? temp, int? util)> ReadTempUtilAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_exe,
                "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (null, null);

            string outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            string? line = outp.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
            if (line == null) return (null, null);
            var parts = line.Split(',');
            int? temp = parts.Length > 0 && int.TryParse(parts[0].Trim(), out int t) ? t : (int?)null;
            int? util = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int u) ? u : (int?)null;
            return (temp, util);
        }
        catch
        {
            return (null, null);
        }
    }
}
