using System.Runtime.InteropServices;

namespace PclMemOpt;

/// <summary>
/// 可执行的内存操作集合，位标志与 PCL-CE 的 MemSwapScope 一一对应。
/// </summary>
[Flags]
internal enum MemSwapScope
{
    None = 0,
    EmptyWorkingSets = 1 << 0,
    FlushFileCache = 1 << 1,
    FlushModifiedList = 1 << 2,
    PurgeStandbyList = 1 << 3,
    PurgeLowPriorityStandbyList = 1 << 4,
    RegistryReconciliation = 1 << 5,
    CombinePhysicalMemory = 1 << 6,
    All = 0b1111111,
}

/// <summary>
/// 内存操作的底层实现，逐行还原自 PCL-CE 的 MemSwapWorks.cs。
/// </summary>
internal static class MemSwap
{
    /// <summary>
    /// SystemMemoryListInformation 的“命令字”。这些数字同样是未文档化的。
    /// </summary>
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    private static void ExecuteMemoryListOperation(int command)
    {
        var handle = GCHandle.Alloc(command, GCHandleType.Pinned);
        try
        {
            NtInterop.SetSystemInformation(
                SystemInformationClass.SystemMemoryListInformation,
                handle.AddrOfPinnedObject(),
                sizeof(int));
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    private static void ExecuteStructureOperation<T>(T structure, SystemInformationClass infoClass) where T : struct
    {
        var handle = GCHandle.Alloc(structure, GCHandleType.Pinned);
        try
        {
            NtInterop.SetSystemInformation(
                infoClass,
                handle.AddrOfPinnedObject(),
                (uint)Marshal.SizeOf(structure));
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    /// <summary>清空各进程的工作集，把物理页换出到页面文件。</summary>
    public static void EmptyWorkingSets() => ExecuteMemoryListOperation(MemoryEmptyWorkingSets);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_FILECACHE_INFORMATION
    {
        public UIntPtr CurrentSize;
        public UIntPtr PeakSize;
        public UIntPtr PageFaultCount;
        public UIntPtr MinimumWorkingSet;
        public UIntPtr MaximumWorkingSet;
        public UIntPtr CurrentSizeIncludingTransitionInPages;
        public UIntPtr PeakSizeIncludingTransitionInPages;
        public UIntPtr TransitionRePurposeCount;
        public UIntPtr Flags;
    }

    /// <summary>刷新系统文件缓存。</summary>
    public static void FlushFileCache()
    {
        var scfi = new SYSTEM_FILECACHE_INFORMATION
        {
            MinimumWorkingSet = uint.MaxValue,
            MaximumWorkingSet = uint.MaxValue,
        };
        ExecuteStructureOperation(scfi, SystemInformationClass.SystemFileCacheInformationEx);
    }

    /// <summary>清空已修改页列表，强制把脏页写回磁盘。</summary>
    public static void FlushModifiedList() => ExecuteMemoryListOperation(MemoryFlushModifiedList);

    /// <summary>清空待机列表（Standby List）—— 效果最明显的操作，也是“占用下降几个 G”的主要来源。</summary>
    public static void PurgeStandbyList() => ExecuteMemoryListOperation(MemoryPurgeStandbyList);

    /// <summary>清空低优先级待机列表。</summary>
    public static void PurgeLowPriorityStandbyList() => ExecuteMemoryListOperation(MemoryPurgeLowPriorityStandbyList);

    /// <summary>注册表配置单元协调。</summary>
    public static void RegistryReconciliation() =>
        NtInterop.SetSystemInformation(
            SystemInformationClass.SystemRegistryReconciliationInformation,
            IntPtr.Zero,
            0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_COMBINE_INFORMATION_EX
    {
        public IntPtr Handle;
        public UIntPtr PagesCombined;
        public uint Flags;
    }

    /// <summary>合并物理内存中的重复页。</summary>
    public static void CombinePhysicalMemory()
    {
        var combineInfoEx = new MEMORY_COMBINE_INFORMATION_EX();
        ExecuteStructureOperation(combineInfoEx, SystemInformationClass.SystemCombinePhysicalMemoryInformation);
    }

    // ───────────────────────── 内存查询 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// 一次内存状态快照。AvailPageFile 是关键指标：内存被换出到页面文件后，
    /// 可用页面文件会明显下降——这正是“优化”的代价所在。
    /// </summary>
    public sealed record MemorySnapshot(
        ulong AvailPhysical,
        ulong TotalPhysical,
        ulong AvailPageFile,
        ulong TotalPageFile,
        uint LoadPercent)
    {
        public ulong UsedPageFile => TotalPageFile - AvailPageFile;

        public static MemorySnapshot Capture()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
                throw new InvalidOperationException("GlobalMemoryStatusEx 调用失败");
            return new MemorySnapshot(
                status.ullAvailPhys,
                status.ullTotalPhys,
                status.ullAvailPageFile,
                status.ullTotalPageFile,
                status.dwMemoryLoad);
        }
    }

    public static string Readable(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{value:0.##} {units[i]}";
    }
}
