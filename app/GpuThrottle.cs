using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LlmOcr;

/// <summary>
/// Closed-loop thermal throttle. While active it polls GPU temperature + utilization; when
/// the GPU is actually computing AND hotter than target it SUSPENDS only the GPU compute
/// process(es) — the PIDs holding a CUDA context, from <c>nvidia-smi --query-compute-apps</c>.
/// It never touches the API server, the PDF-render helper processes, or anything else, so it
/// can't wedge the pipeline or trip render/poll deadlines. Suspending pauses the CPU threads;
/// in-flight CUDA kernels drain and the GPU cools. Everything is resumed on stop / cooldown.
/// </summary>
public sealed class GpuThrottle
{
    [DllImport("ntdll.dll")] private static extern uint NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern uint NtResumeProcess(IntPtr processHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    private const int PollMs = 2500;         // how often we sample temp/utilization
    private const int Hysteresis = 3;        // resume once temp <= target - this
    private const int NoLimitC = 90;         // target >= this => never throttle
    private const int MaxSuspendMs = 60_000; // safety: never keep compute paused longer than this
    private const int UtilBusy = 25;         // only throttle when GPU utilization >= this (%)

    private readonly GpuMonitor _gpu;
    private readonly Func<int> _targetTempC;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    // Every PID we have suspended in the current cooldown (union), so all get resumed.
    private readonly HashSet<int> _suspended = new();
    private readonly object _lock = new();

    public GpuThrottle(GpuMonitor gpu, Func<int> targetTempC, Action<string>? log = null)
    {
        _gpu = gpu;
        _targetTempC = targetTempC;
        _log = log;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _loop?.Wait(3000); } catch { /* ignore */ }
        ResumeAll();
        _cts?.Dispose(); _cts = null; _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        bool cooling = false;
        DateTime suspendedAt = DateTime.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int target = _targetTempC();
                var (temp, util) = await _gpu.ReadTempUtilAsync(ct);

                if (target >= NoLimitC || temp is not int t)
                {
                    if (cooling) { ResumeAll(); cooling = false; }
                }
                // Suspend only when the GPU is busy AND too hot.
                else if (!cooling && t > target && (util ?? 100) >= UtilBusy)
                {
                    var pids = await _gpu.ReadComputePidsAsync(ct);
                    if (pids.Count > 0)
                    {
                        Suspend(pids);
                        cooling = true; suspendedAt = DateTime.UtcNow;
                        _log?.Invoke($"GPU {t}°C > цель {target}°C — приостанавливаю до остывания");
                    }
                }
                else if (cooling && (t <= target - Hysteresis
                                     || (DateTime.UtcNow - suspendedAt).TotalMilliseconds >= MaxSuspendMs))
                {
                    ResumeAll(); cooling = false;
                    _log?.Invoke($"GPU {t}°C — продолжаю");
                }
                else if (cooling)
                {
                    // still too hot: catch any newly spawned compute process
                    var pids = await _gpu.ReadComputePidsAsync(ct);
                    if (pids.Count > 0) Suspend(pids);
                }

                if (ct.WaitHandle.WaitOne(PollMs)) break;
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally { ResumeAll(); }
    }

    private void Suspend(List<int> pids)
    {
        lock (_lock)
            foreach (int pid in pids)
                if (_suspended.Add(pid)) Act(pid, suspend: true);
    }

    private void ResumeAll()
    {
        lock (_lock)
        {
            foreach (int pid in _suspended) Act(pid, suspend: false);
            _suspended.Clear();
        }
    }

    private static void Act(int pid, bool suspend)
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return;
        try { if (suspend) NtSuspendProcess(h); else NtResumeProcess(h); }
        catch { /* process may have exited */ }
        finally { CloseHandle(h); }
    }
}
