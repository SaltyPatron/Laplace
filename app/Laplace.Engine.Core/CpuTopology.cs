using System.Numerics;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Hybrid CPU topology for ingest worker defaults. On Intel P/E CPUs, CPU-bound native work
/// (decompose, compose) should scale to performance-core count, not logical processor count.
/// Env overrides: <c>LAPLACE_P_CORES</c>, <c>LAPLACE_CPU_BOUND_WORKERS</c>.
/// </summary>
public static class CpuTopology
{
    public static int PerformanceCoreCount => Current.PerformanceCoreCount;
    public static int EfficientCoreCount => Current.EfficientCoreCount;
    public static int LogicalProcessorCount => Current.LogicalProcessorCount;
    public static bool IsHybrid => Current.IsHybrid;

    internal static CpuSnapshot? TestOverride;

    private static CpuSnapshot Current => TestOverride ?? LazySnapshot.Value;

    private static readonly Lazy<CpuSnapshot> LazySnapshot = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int ResolveCpuBoundWorkers(int headroom = 1, int maxCap = 16)
    {
        if (TryParsePositiveEnv("LAPLACE_CPU_BOUND_WORKERS", out int hard))
            return hard;
        return Math.Clamp(PerformanceCoreCount - headroom, 1, maxCap);
    }

    public static int ResolveIoBoundWorkers(int defaultCap = 8) =>
        Math.Clamp(Math.Min(LogicalProcessorCount, defaultCap), 1, defaultCap);

    /// <summary>Native apply_batch / Hilbert COPY fan-out — CPU-bound on P-cores, not E-cores.</summary>
    public static int ResolveApplyPartitions()
    {
        if (TryParsePositiveEnv("LAPLACE_APPLY_PARTITIONS", out int hard))
            return Math.Min(hard, 64);
        return ResolveCpuBoundWorkers(headroom: 0, maxCap: Math.Max(PerformanceCoreCount, 1));
    }

    /// <summary>
    /// Hybrid ingest entry: refuse to run if P-core affinity cannot be established. No silent
    /// scheduler scatter onto E-cores and no "just run serial" degradation.
    /// </summary>
    public static void EnsurePerformanceCoreExecution()
    {
        if (!IsHybrid) return;
        if (PerformanceCoreCpuIndices.Count == 0)
            throw new InvalidOperationException(
                "Hybrid CPU detected but P-core logical processor indices are unknown. "
                + "Ingest refuses to run on the OS scheduler (E-core scatter). "
                + "Set LAPLACE_P_CORES or fix CpuTopology detection.");
        if (!PinCurrentThreadToPerformanceCores())
            throw new InvalidOperationException(
                "Hybrid CPU: SetThreadAffinityMask to P-core set failed. "
                + "Ingest refuses unpinned execution.");
    }

    /// <summary>Pin current thread to P-cores; on hybrid boxes failure is fatal (see Ensure).</summary>
    public static void RequirePerformanceCorePin() => EnsurePerformanceCoreExecution();

    /// <summary>
    /// Logical-processor indices that belong to performance (P) cores. Empty when the topology is
    /// not hybrid or could not be detected (in which case pinning is a no-op and work simply runs
    /// unpinned across all cores). Detected on BOTH OSes: Windows via the same
    /// GetLogicalProcessorInformationEx walk; Linux via /sys/devices/cpu_core/cpus.
    /// </summary>
    public static IReadOnlyList<int> PerformanceCoreCpuIndices => PCoreIndices.Value;

    internal static int[]? TestPCoreIndicesOverride;

    private static readonly Lazy<int[]> PCoreIndices =
        new(DetectPerformanceCoreIndices, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Pin the CURRENT OS thread to the performance-core set. On hybrid CPUs callers must use
    /// <see cref="RequirePerformanceCorePin"/> at ingest/compute entry — silent unpinned execution
    /// is not acceptable on P/E silicon.
    /// </summary>
    public static bool PinCurrentThreadToPerformanceCores()
    {
        var idx = PerformanceCoreCpuIndices;
        if (idx.Count == 0) return false;

        if (OperatingSystem.IsWindows())
            return TryPinWindows(idx);
        if (OperatingSystem.IsLinux())
            return TryPinLinux(idx);
        return false;
    }

    /// <summary>
    /// Run <paramref name="body"/> over [0, count) on <paramref name="workers"/> P-core-pinned
    /// dedicated threads (never ThreadPool — the hybrid scheduler parks those on E-cores).
    /// </summary>
    public static void RunPinnedParallel(int count, int workers, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        workers = Math.Clamp(workers, 1, count);

        if (workers == 1)
        {
            RequirePerformanceCorePin();
            for (int i = 0; i < count; i++) body(i);
            return;
        }

        var threads = new Thread[workers];
        var next = -1;
        Exception? failure = null;
        for (int w = 0; w < workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                RequirePerformanceCorePin();
                int i;
                while ((i = Interlocked.Increment(ref next)) < count)
                {
                    try { body(i); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); return; }
                }
            }) { IsBackground = true, Name = $"laplace-pcore-{w}" };
            threads[w].Start();
        }
        foreach (var t in threads) t.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>Run <paramref name="count"/> independent async jobs on pinned dedicated threads.</summary>
    public static async Task RunPinnedAsyncParallel(int count, Func<int, CancellationToken, Task> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        var threads = new Thread[count];
        var errors = new Exception?[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            threads[idx] = new Thread(() =>
            {
                try
                {
                    RequirePerformanceCorePin();
                    body(idx, ct).GetAwaiter().GetResult();
                }
                catch (Exception ex) { errors[idx] = ex; }
            }) { IsBackground = true, Name = $"laplace-pcore-async-{idx}" };
            threads[idx].Start();
        }
        foreach (var t in threads) t.Join();
        ct.ThrowIfCancellationRequested();
        foreach (var ex in errors)
            if (ex is not null) throw ex;
        await Task.CompletedTask;
    }

    /// <summary>Run one async body on a dedicated P-core-pinned thread (never ThreadPool).</summary>
    public static Task RunOnPinnedThread(Func<CancellationToken, Task> body, string threadName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RequirePerformanceCorePin();
                body(ct).GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = threadName };
        thread.Start();
        return tcs.Task;
    }

    private static int[] DetectPerformanceCoreIndices()
    {
        if (TestPCoreIndicesOverride is not null) return TestPCoreIndicesOverride;
        try
        {
            if (OperatingSystem.IsWindows() && TryDetectWindowsPCoreIndices(out var win))
                return win;
            if (OperatingSystem.IsLinux() && TryDetectLinuxPCoreIndices(out var lin))
                return lin;
        }
        catch { /* detection is best-effort; unknown => no pinning */ }
        return Array.Empty<int>();
    }

    internal static CpuSnapshot Detect() => DetectPlatform();

    private static CpuSnapshot DetectPlatform()
    {
        int logical = Environment.ProcessorCount;
        if (TryParsePositiveEnv("LAPLACE_P_CORES", out int pinnedP))
            return new CpuSnapshot(pinnedP, 0, logical, IsHybrid: false);

        if (OperatingSystem.IsWindows())
        {
            if (TryDetectWindowsHybrid(out int pCores, out int eCores, out bool hybrid))
                return new CpuSnapshot(Math.Max(1, pCores), Math.Max(0, eCores), logical, hybrid);
        }

        return new CpuSnapshot(logical, 0, logical, IsHybrid: false);
    }

    private static bool TryDetectWindowsHybrid(out int performanceCores, out int efficientCores, out bool isHybrid)
    {
        performanceCores = 0;
        efficientCores = 0;
        isHybrid = false;

        const int RelationProcessorCore = 0;
        uint size = 0;
        if (!GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size)
            && Marshal.GetLastWin32Error() != 122)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))
                return false;

            int offset = 0;
            var cores = new List<(byte Eff, int LpCount)>();

            while (offset < (int)size)
            {
                var header = Marshal.PtrToStructure<LogicalProcessorInfoHeader>(buffer + offset);
                if (header.Relationship == RelationProcessorCore)
                {
                    int relOffset = offset + Marshal.SizeOf<LogicalProcessorInfoHeader>();
                    byte eff = Marshal.ReadByte(buffer, relOffset + 1);
                    int lpCount = ReadCoreLogicalProcessorCount(buffer, relOffset);
                    cores.Add((eff, lpCount));
                }
                if (header.Size == 0)
                    break;
                offset += (int)header.Size;
            }

            if (cores.Count == 0)
                return false;

            byte minClass = cores.Min(c => c.Eff);
            byte maxClass = cores.Max(c => c.Eff);
            int maxLp = cores.Max(c => c.LpCount);
            int minLp = cores.Where(c => c.LpCount > 0).Select(c => c.LpCount).DefaultIfEmpty(maxLp).Min();

            if (maxLp > minLp)
            {
                isHybrid = true;
                performanceCores = cores.Count(c => c.LpCount == maxLp);
                efficientCores = cores.Count(c => c.LpCount == minLp);
                return performanceCores > 0;
            }

            if (minClass != maxClass)
            {
                isHybrid = true;
                performanceCores = cores.Count(c => c.Eff == maxClass);
                efficientCores = cores.Count(c => c.Eff == minClass);
                return performanceCores > 0;
            }

            performanceCores = cores.Count;
            efficientCores = 0;
            isHybrid = false;
            return performanceCores > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadCoreLogicalProcessorCount(IntPtr buffer, int relationshipOffset)
    {
        ushort groupCount = (ushort)Marshal.ReadInt16(buffer, relationshipOffset + 22);
        if (groupCount == 0)
            return 0;

        int lpCount = 0;
        int groupOffset = relationshipOffset + 24;
        for (int g = 0; g < groupCount; g++)
        {
            int entryOffset = groupOffset + g * 16;
            ulong mask = (ulong)Marshal.ReadInt64(buffer, entryOffset);
            lpCount += BitOperations.PopCount(mask);
        }
        return lpCount;
    }

    private static bool TryParsePositiveEnv(string name, out int value)
    {
        value = 0;
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out value) && value > 0;
    }

    // ---- Performance-core logical-processor index detection (P-core mask) ----

    private static bool TryDetectWindowsPCoreIndices(out int[] indices)
    {
        indices = Array.Empty<int>();
        const int RelationProcessorCore = 0;
        uint size = 0;
        if (!GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size)
            && Marshal.GetLastWin32Error() != 122)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))
                return false;

            int offset = 0;
            // Per core: (group-0 affinity mask, LP count). All LPs are assumed in processor group 0;
            // boxes with >64 logical processors use multiple groups and are left unpinned (rare on
            // the hybrid desktop SKUs this targets).
            var cores = new List<(ulong Mask, int LpCount, int Group)>();
            while (offset < (int)size)
            {
                var header = Marshal.PtrToStructure<LogicalProcessorInfoHeader>(buffer + offset);
                if (header.Relationship == RelationProcessorCore)
                {
                    int relOffset = offset + Marshal.SizeOf<LogicalProcessorInfoHeader>();
                    ushort groupCount = (ushort)Marshal.ReadInt16(buffer, relOffset + 22);
                    if (groupCount >= 1)
                    {
                        int groupOffset = relOffset + 24;       // first GROUP_AFFINITY entry
                        ulong mask = (ulong)Marshal.ReadInt64(buffer, groupOffset);
                        ushort group = (ushort)Marshal.ReadInt16(buffer, groupOffset + 8);
                        int lp = 0;
                        for (int g = 0; g < groupCount; g++)
                            lp += BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer, groupOffset + g * 16));
                        cores.Add((mask, lp, group));
                    }
                }
                if (header.Size == 0) break;
                offset += (int)header.Size;
            }
            if (cores.Count == 0) return false;

            int maxLp = cores.Max(c => c.LpCount);
            int minLp = cores.Where(c => c.LpCount > 0).Select(c => c.LpCount).DefaultIfEmpty(maxLp).Min();
            if (maxLp == minLp) return false; // not hybrid: nothing to pin to

            var idx = new List<int>();
            foreach (var c in cores)
            {
                if (c.LpCount != maxLp || c.Group != 0) continue; // P-cores in group 0
                ulong m = c.Mask;
                while (m != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(m);
                    idx.Add(bit);
                    m &= m - 1;
                }
            }
            if (idx.Count == 0) return false;
            idx.Sort();
            indices = idx.ToArray();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryDetectLinuxPCoreIndices(out int[] indices)
    {
        indices = Array.Empty<int>();
        // Intel hybrid exposes P-cores and E-cores as separate CPU PMUs:
        //   /sys/devices/cpu_core/cpus  -> P-core logical CPUs (e.g. "0-15")
        //   /sys/devices/cpu_atom/cpus  -> E-core logical CPUs
        // Non-hybrid kernels expose neither; we then leave the set empty (no pinning).
        const string pcorePath = "/sys/devices/cpu_core/cpus";
        if (!File.Exists(pcorePath)) return false;
        try
        {
            string raw = File.ReadAllText(pcorePath).Trim();
            var idx = ParseCpuList(raw);
            if (idx.Length == 0) return false;
            indices = idx;
            return true;
        }
        catch { return false; }
    }

    // Parse a Linux cpulist like "0-7,16,18-19" into explicit indices.
    internal static int[] ParseCpuList(string s)
    {
        var result = new List<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, out int v)) result.Add(v);
            }
            else if (int.TryParse(part[..dash], out int lo) && int.TryParse(part[(dash + 1)..], out int hi))
            {
                for (int i = lo; i <= hi; i++) result.Add(i);
            }
        }
        result.Sort();
        return result.ToArray();
    }

    // ---- Affinity application (verified) ----

    private static bool TryPinWindows(IReadOnlyList<int> cpuIndices)
    {
        ulong mask = 0;
        foreach (int i in cpuIndices)
        {
            if (i >= 64) return false; // single-group affinity only
            mask |= 1UL << i;
        }
        if (mask == 0) return false;
        IntPtr prior = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);
        return prior != IntPtr.Zero; // 0 == failure per Win32 contract
    }

    private static bool TryPinLinux(IReadOnlyList<int> cpuIndices)
    {
        // cpu_set_t is a 1024-bit bitmap (128 bytes). Set the P-core bits and apply to the calling
        // thread (pid 0 == current thread for sched_setaffinity).
        const int setBytes = 128;
        var set = new byte[setBytes];
        foreach (int i in cpuIndices)
        {
            if (i < 0 || i >= setBytes * 8) continue;
            set[i >> 3] |= (byte)(1 << (i & 7));
        }
        int rc = sched_setaffinity(0, (IntPtr)setBytes, set);
        return rc == 0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, byte[] mask);

    internal readonly record struct CpuSnapshot(
        int PerformanceCoreCount,
        int EfficientCoreCount,
        int LogicalProcessorCount,
        bool IsHybrid);

    [StructLayout(LayoutKind.Sequential)]
    private struct LogicalProcessorInfoHeader
    {
        public int Relationship;
        public uint Size;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
