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

    // Used to find the PID that owns the mineru-api TCP port so it is never suspended
    // (keeps the HTTP endpoint responsive to the client's task-status polls).
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwSize, bool sort,
        int ipVersion, int tblClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_LISTEN = 2;

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

    private const int PollMs = 2500;       // how often we sample temp/utilization
    private const int Hysteresis = 3;      // resume once temp <= target - this (avoid flapping)
    private const int NoLimitC = 90;       // target >= this => never throttle
    private const int MaxSuspendMs = 90_000; // safety: never keep the tree paused longer than this
    private const int UtilBusy = 25;       // only throttle when GPU utilization >= this (%)
                                           // so CPU-bound phases (PDF render) are never paused

    private readonly Func<int?> _rootPid;
    private readonly int _serverPort;
    private readonly GpuMonitor _gpu;
    private readonly Func<int> _targetTempC;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile List<int> _suspended = new();

    public GpuThrottle(Func<int?> rootPid, int serverPort, GpuMonitor gpu, Func<int> targetTempC, Action<string>? log = null)
    {
        _rootPid = rootPid;
        _serverPort = serverPort;
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
                var (temp, util) = await _gpu.ReadTempUtilAsync(ct);

                // No sensor or "no limit" -> make sure everything runs.
                if (target >= NoLimitC || temp is not int t)
                {
                    if (cooling) { ResumeTracked(); cooling = false; }
                }
                // Throttle only while the GPU is actually computing AND too hot — never during
                // CPU-bound phases (PDF render / IO), which carry their own wall-clock deadlines.
                else if (!cooling && t > target && (util ?? 100) >= UtilBusy)
                {
                    SuspendWorkers();
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
                    // still too hot: re-assert suspend on any newly spawned worker children
                    SuspendWorkers();
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

    /// <summary>Suspends the tree EXCEPT the process that owns the mineru-api TCP port,
    /// so the HTTP server keeps answering the client's task-status polls while the GPU
    /// worker processes are paused to cool down.</summary>
    private void SuspendWorkers()
    {
        var pids = Tree();
        int? keep = PortOwnerPid();
        if (keep is int k) pids.RemoveAll(p => p == k);
        SetState(pids, suspend: true);
    }

    /// <summary>PID listening on 127.0.0.1:_serverPort, or null if not found.</summary>
    private int? PortOwnerPid()
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return null;
            int n = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            IntPtr row = buf + 4;
            for (int i = 0; i < n; i++)
            {
                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                int port = (int)(((r.localPort & 0xFF) << 8) | ((r.localPort >> 8) & 0xFF));
                if (r.state == MIB_TCP_STATE_LISTEN && port == _serverPort) return (int)r.owningPid;
                row += rowSize;
            }
        }
        catch { /* fall through */ }
        finally { Marshal.FreeHGlobal(buf); }
        return null;
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
