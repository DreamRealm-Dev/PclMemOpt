using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PclMemOpt;

/// <summary>
/// NT 权限常量。取值与 PCL-CE 的 NtInterop.SePrivilege 完全一致。
/// </summary>
internal enum SePrivilege : uint
{
    SeIncreaseQuotaPrivilege = 5,
    SeProfileSingleProcessPrivilege = 13,
}

/// <summary>
/// 未文档化的 SYSTEM_INFORMATION_CLASS 取值。
/// 这些数字在微软文档中从未公开，属于逆向所得，跨 Windows 版本存在变更风险
/// （这正是 PCL-CE 移除该功能的官方理由之一）。
/// </summary>
internal enum SystemInformationClass
{
    SystemMemoryListInformation = 80,
    SystemFileCacheInformationEx = 81,
    SystemCombinePhysicalMemoryInformation = 130,
    SystemRegistryReconciliationInformation = 155,
}

/// <summary>NTSTATUS 非 0 时抛出，保留原始状态码便于排查。</summary>
internal sealed class NtException : Exception
{
    public uint NtStatus { get; }
    public uint DosError { get; }
    public string Operation { get; }

    public NtException(string operation, uint ntStatus, uint dosError)
        : base($"{operation} 失败：NTSTATUS=0x{ntStatus:X8}, Win32={dosError} ({new Win32Exception((int)dosError).Message})")
    {
        Operation = operation;
        NtStatus = ntStatus;
        DosError = dosError;
    }
}

internal static class NtInterop
{
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlAdjustPrivilege(
        SePrivilege privilege,
        [MarshalAs(UnmanagedType.U1)] bool enable,
        [MarshalAs(UnmanagedType.U1)] bool currentThread,
        [MarshalAs(UnmanagedType.U1)] out bool wasEnabled);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(uint status);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint NtSetSystemInformation(
        SystemInformationClass systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern void RtlGetNtVersionNumbers(out int major, out int minor, out int build);

    /// <summary>获取内核真实版本号（不受兼容性设置影响）。</summary>
    public static Version GetOsVersion()
    {
        RtlGetNtVersionNumbers(out var major, out var minor, out var build);
        build &= 0xFFFF;
        return new Version(major, minor, build);
    }

    /// <summary>
    /// 调整当前线程/进程的特权。必须在调用 NtSetSystemInformation 之前成功执行，
    /// 否则内存操作会静默失败（返回 0 字节优化量）。
    /// </summary>
    public static void EnablePrivilege(SePrivilege privilege)
    {
        var status = RtlAdjustPrivilege(privilege, true, false, out _);
        if (status != 0)
            throw new NtException($"启用特权 {privilege}", status, RtlNtStatusToDosError(status));
    }

    public static void SetSystemInformation(SystemInformationClass infoClass, IntPtr info, uint infoLength)
    {
        var status = NtSetSystemInformation(infoClass, info, infoLength);
        if (status != 0)
            throw new NtException($"NtSetSystemInformation({infoClass})", status, RtlNtStatusToDosError(status));
    }
}
