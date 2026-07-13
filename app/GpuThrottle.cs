using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LlmOcr;

/// <summary>
/// Closed-loop thermal throttle for an external process tree (mineru-api + workers).
/// While active it polls the GPU temperature; when it exceeds the target it SUSPENDS the
/// tree (CPU threads paused → in-flight CUDA kernels drain → GPU idles and cools), and
/// resumes once the temperature drops back below target (with hysteresis). Same idea as
/// llmtranslate's between-chunk cooldown, adapted to a black-box child process. No admin.
/// The tree is ALWAYS left resumed on stop.
/// </summary>
public sealed class GpuThrottle
{
    [DllImport("ntdll.dll")] private static extern uint NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern uint NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private const int PollMs = 2500;       // how often we sample the temperature
    private const int Hysteresis = 3;      // resume once temp <= target - this (avoid flapping)
    private const int NoLimitC = 90;       // target >= this => never throttle
    private const int MaxSuspendMs = 90_000; // safety: never keep the tree paused longer than this

    private readonly Func<int?> _rootPid;
    private readonly GpuMonitor _gpu;
    private readonly Func<int> _targetTempC;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile List<int> _suspended = new();

    public GpuThrottle(Func<int?> rootPid, GpuMonitor gpu, Func<int> targetTempC, Action<string>? log = null)
    {
        _rootPid = rootPid;
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
        ResumeTracked();
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
                int? temp = await _gpu.ReadTempAsync(ct);

                // No sensor or "no limit" -> make sure everything runs.
                if (target >= NoLimitC || temp is not int t)
                {
                    if (cooling) { ResumeTracked(); cooling = false; }
                }
                else if (!cooling && t > target)
                {
                    SetState(Tree(), suspend: true);
                    cooling = true; suspendedAt = DateTime.UtcNow;
                    _log?.Invoke($"GPU {t}°C > цель {target}°C — приостанавливаю до остывания");
                }
                else if (cooling && (t <= target - Hysteresis
                                     || (DateTime.UtcNow - suspendedAt).TotalMilliseconds >= MaxSuspendMs))
                {
                    ResumeTracked(); cooling = false;
                    _log?.Invoke($"GPU {t}°C — продолжаю");
                }
                else if (cooling)
                {
                    // still too hot: re-assert suspend on any newly spawned children
                    SetState(Tree(), suspend: true);
                }

                if (ct.WaitHandle.WaitOne(PollMs)) break;
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally { ResumeTracked(); }
    }

    /// <summary>All PIDs in the tree rooted at the target process (via a process snapshot).</summary>
    private List<int> Tree()
    {
        var result = new List<int>();
        if (_rootPid() is not int root) return result;

        var children = new Dictionary<int, List<int>>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) { result.Add(root); return result; }
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
            {
                do
                {
                    int ppid = (int)e.th32ParentProcessID;
                    if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new List<int>();
                    list.Add((int)e.th32ProcessID);
                } while (Process32Next(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }

        var queue = new Queue<int>();
        var seen = new HashSet<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!seen.Add(p)) continue;
            result.Add(p);
            if (children.TryGetValue(p, out var kids))
                foreach (int k in kids) queue.Enqueue(k);
        }
        return result;
    }

    private void SetState(List<int> pids, bool suspend)
    {
        foreach (int pid in pids) Act(pid, suspend);
        if (suspend) _suspended = pids;
    }

    private void ResumeTracked()
    {
        var snapshot = _suspended;
        foreach (int pid in snapshot) Act(pid, suspend: false);
        _suspended = new List<int>();
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
