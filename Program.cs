using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using PclMemOpt;

// 让中文在控制台正常显示
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch
{
    /* 已重定向输出时可能失败，忽略 */
}

return App.Run(args);

internal static class App
{
    private const string Version = "1.0.0";

    private sealed class Options
    {
        public MemSwapScope Scope = MemSwapScope.All;
        public bool ScopeExplicit;
        public int LoopSeconds;
        public bool NoElevate;
        public bool DryRun;
        public bool Quiet;
        public bool ShowHelp;
    }

    public static int Run(string[] args)
    {
        var opts = Parse(args);

        if (opts.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        // --dry-run 只打印计划，不需要提权，因此放在提权检查之前
        if (opts.DryRun)
        {
            PrintPlan(opts.Scope);
            return 0;
        }

        // ── 提权：原实现在 PCL-CE 中是单开一个提权子进程来执行的，
        //    独立工具直接自我重启为管理员更简洁。
        if (!opts.NoElevate && !IsAdministrator())
        {
            if (!opts.Quiet)
            {
                Console.WriteLine("需要管理员权限，正在请求提权……");
            }
            if (!TryRelaunchElevated(args))
            {
                Console.Error.WriteLine("提权被拒绝或失败。请以管理员身份运行，或加 --no-elevate 强制继续（但操作会失败）。");
                return 5;
            }
            return 0;
        }

        if (opts.LoopSeconds > 0)
        {
            return RunLoop(opts);
        }

        return RunOnce(opts);
    }

    // ───────────────────────── 单次执行 ─────────────────────────

    private static int RunOnce(Options opts)
    {
        Console.WriteLine($"PclMemOpt {Version} — PCL-CE「内存优化」的独立复刻实现");
        Console.WriteLine($"内核版本 {NtInterop.GetOsVersion()}    管理员: {(IsAdministrator() ? "是" : "否")}");
        Console.WriteLine();

        SePrivilege[] requiredPrivileges =
        [
            SePrivilege.SeProfileSingleProcessPrivilege,
            SePrivilege.SeIncreaseQuotaPrivilege,
        ];

        foreach (var privilege in requiredPrivileges)
        {
            try
            {
                NtInterop.EnablePrivilege(privilege);
            }
            catch (NtException ex)
            {
                Console.Error.WriteLine($"× 特权获取失败 -> {ex.Message}");
                Console.Error.WriteLine("  后续操作极可能静默失败（只会得到几百字节的“优化量”），已中止。");
                return 3;
            }
        }

        var before = MemSwap.MemorySnapshot.Capture();
        PrintSnapshot("优化前", before);

        var (succeeded, failed, elapsed) = ExecuteScope(opts.Scope, opts.Quiet);

        Console.WriteLine();
        var after = MemSwap.MemorySnapshot.Capture();
        PrintSnapshot("优化后", after);

        Console.WriteLine();
        var deltaAvail = (long)after.AvailPhysical - (long)before.AvailPhysical;
        var deltaPage = (long)after.UsedPageFile - (long)before.UsedPageFile;

        Console.WriteLine($"结果: 可用物理内存 {(deltaAvail >= 0 ? "+" : "-")}{MemSwap.Readable((ulong)Math.Abs(deltaAvail))}" +
                          $"    耗时 {elapsed.TotalSeconds:0.00} s" +
                          $"    成功 {succeeded}/{succeeded + failed} 项");

        if (deltaPage > 0)
        {
            Console.WriteLine($"代价: 页面文件多占用 {MemSwap.Readable((ulong)deltaPage)}（内存被换出到硬盘，这部分并没有“省下来”）");
        }

        if (failed > 0)
        {
            Console.WriteLine($"注意: 有 {failed} 项操作失败，详见上方 ✗ 行。");
            return 4;
        }

        return 0;
    }

    // ───────────────────────── 循环执行 ─────────────────────────

    private static int RunLoop(Options opts)
    {
        Console.WriteLine($"PclMemOpt {Version} — 循环模式，每 {opts.LoopSeconds} 秒执行一次，Ctrl+C 退出");
        Console.WriteLine();

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        while (!stop.IsSet)
        {
            var (_, failed, elapsed) = ExecuteScope(opts.Scope, quiet: true);
            var snap = MemSwap.MemorySnapshot.Capture();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 可用物理 {MemSwap.Readable(snap.AvailPhysical)}" +
                              $" (占用 {snap.LoadPercent}%)" +
                              $"  页面文件已用 {MemSwap.Readable(snap.UsedPageFile)}" +
                              $"  耗时 {elapsed.TotalMilliseconds:0} ms" +
                              (failed > 0 ? $"  失败 {failed} 项" : ""));

            stop.Wait(TimeSpan.FromSeconds(opts.LoopSeconds));
        }

        Console.WriteLine("已退出。");
        return 0;
    }

    // ───────────────────────── 执行与报告 ─────────────────────────

    private static readonly (MemSwapScope Scope, string Name)[] AllOperations =
    [
        (MemSwapScope.EmptyWorkingSets, "清空工作集"),
        (MemSwapScope.FlushFileCache, "刷新文件缓存"),
        (MemSwapScope.FlushModifiedList, "清空已修改页列表"),
        (MemSwapScope.PurgeStandbyList, "清空待机列表"),
        (MemSwapScope.PurgeLowPriorityStandbyList, "清空低优先级待机列表"),
        (MemSwapScope.RegistryReconciliation, "注册表协调"),
        (MemSwapScope.CombinePhysicalMemory, "合并物理内存"),
    ];

    private static (int Succeeded, int Failed, TimeSpan Elapsed) ExecuteScope(MemSwapScope scope, bool quiet)
    {
        var selected = AllOperations.Where(op => scope.HasFlag(op.Scope)).ToArray();
        var succeeded = 0;
        var failed = 0;
        var total = Stopwatch.StartNew();
        var index = 0;

        foreach (var (opScope, name) in selected)
        {
            index++;
            var prefix = quiet ? "" : $"  [{index}/{selected.Length}] ";
            if (!quiet) Console.Write($"{prefix}{name,-22}");
            var sw = Stopwatch.StartNew();
            try
            {
                Invoke(opScope);
                sw.Stop();
                succeeded++;
                if (!quiet) Console.WriteLine($"OK      {sw.ElapsedMilliseconds,5} ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                if (!quiet) Console.WriteLine($"✗ 失败  {sw.ElapsedMilliseconds,5} ms");
                Console.Error.WriteLine($"       {ex.Message}");
            }
        }

        total.Stop();
        return (succeeded, failed, total.Elapsed);
    }

    private static void Invoke(MemSwapScope scope)
    {
        // 顺序与 PCL-CE 的 _OnMemorySwap 保持一致
        if (scope.HasFlag(MemSwapScope.EmptyWorkingSets)) MemSwap.EmptyWorkingSets();
        if (scope.HasFlag(MemSwapScope.FlushFileCache)) MemSwap.FlushFileCache();
        if (scope.HasFlag(MemSwapScope.FlushModifiedList)) MemSwap.FlushModifiedList();
        if (scope.HasFlag(MemSwapScope.PurgeStandbyList)) MemSwap.PurgeStandbyList();
        if (scope.HasFlag(MemSwapScope.PurgeLowPriorityStandbyList)) MemSwap.PurgeLowPriorityStandbyList();
        if (scope.HasFlag(MemSwapScope.RegistryReconciliation)) MemSwap.RegistryReconciliation();
        if (scope.HasFlag(MemSwapScope.CombinePhysicalMemory)) MemSwap.CombinePhysicalMemory();
    }

    private static void PrintSnapshot(string label, MemSwap.MemorySnapshot snap)
    {
        Console.WriteLine($"{label}  可用物理内存 {MemSwap.Readable(snap.AvailPhysical)} / {MemSwap.Readable(snap.TotalPhysical)}" +
                          $" (占用 {snap.LoadPercent}%)" +
                          $"    页面文件已用 {MemSwap.Readable(snap.UsedPageFile)}");
    }

    private static void PrintPlan(MemSwapScope scope)
    {
        Console.WriteLine("将执行以下操作（--dry-run，未实际执行）：");
        var i = 0;
        foreach (var (opScope, name) in AllOperations.Where(op => scope.HasFlag(op.Scope)))
        {
            Console.WriteLine($"  [{++i}] {name}  ({opScope})");
        }
    }

    // ───────────────────────── 参数解析 ─────────────────────────

    private static Options Parse(string[] args)
    {
        var opts = new Options { Scope = MemSwapScope.None };

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help" or "/?":
                    opts.ShowHelp = true;
                    return opts;
                case "--all":
                    opts.Scope |= MemSwapScope.All;
                    opts.ScopeExplicit = true;
                    break;
                case "-w" or "--working-set":
                    opts.Scope |= MemSwapScope.EmptyWorkingSets;
                    opts.ScopeExplicit = true;
                    break;
                case "-f" or "--file-cache":
                    opts.Scope |= MemSwapScope.FlushFileCache;
                    opts.ScopeExplicit = true;
                    break;
                case "-m" or "--modified-list":
                    opts.Scope |= MemSwapScope.FlushModifiedList;
                    opts.ScopeExplicit = true;
                    break;
                case "-s" or "--standby":
                    opts.Scope |= MemSwapScope.PurgeStandbyList;
                    opts.ScopeExplicit = true;
                    break;
                case "--standby-low":
                    opts.Scope |= MemSwapScope.PurgeLowPriorityStandbyList;
                    opts.ScopeExplicit = true;
                    break;
                case "-r" or "--registry":
                    opts.Scope |= MemSwapScope.RegistryReconciliation;
                    opts.ScopeExplicit = true;
                    break;
                case "-c" or "--combine":
                    opts.Scope |= MemSwapScope.CombinePhysicalMemory;
                    opts.ScopeExplicit = true;
                    break;
                case "--loop":
                {
                    var seconds = 0;
                    if (++i >= args.Length || !int.TryParse(args[i], out seconds) || seconds < 1)
                    {
                        Console.Error.WriteLine("--loop 需要一个 >= 1 的整数秒数");
                        Environment.Exit(2);
                    }
                    opts.LoopSeconds = seconds;
                    break;
                }
                case "--no-elevate":
                    opts.NoElevate = true;
                    break;
                case "--dry-run":
                    opts.DryRun = true;
                    break;
                case "-q" or "--quiet":
                    opts.Quiet = true;
                    break;
                default:
                    Console.Error.WriteLine($"未知参数: {a}（用 --help 查看用法）");
                    Environment.Exit(2);
                    break;
            }
        }

        if (!opts.ScopeExplicit) opts.Scope = MemSwapScope.All;
        return opts;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            PclMemOpt {Version} — PCL-CE「内存优化」的独立复刻实现
            使用未文档化的 NT 接口 (NtSetSystemInformation)，需要管理员权限。

            用法: PclMemOpt.exe [选项]

            不指定任何操作时，等价于 --all。

            操作选择:
              -w, --working-set     清空工作集（把物理页换出到页面文件）
              -f, --file-cache      刷新系统文件缓存
              -m, --modified-list   清空已修改页列表（强制脏页写盘）
              -s, --standby         清空待机列表（效果最明显）
                  --standby-low     清空低优先级待机列表
              -r, --registry        注册表配置单元协调
              -c, --combine         合并物理内存中的重复页
                  --all             执行以上全部

            运行方式:
                  --loop <秒>       循环执行，适合挂在后台
                  --dry-run         只列出将要执行的操作
                  --no-elevate      不自动请求管理员权限
              -q, --quiet           安静模式（循环模式下始终安静）
              -h, --help            显示本帮助

            退出码:
              0 成功   2 参数错误   3 特权获取失败   4 有操作失败   5 提权被拒绝

            警告: 该功能主要通过清空工作集、文件缓存与待机列表来拉高“可用物理内存”，
                  代价是文件缓存失效（后续读盘变慢）、脏页写盘增加 SSD 写入量。
                  它不会真正降低内存压力，不建议高频自动执行。
            """);
    }

    // ───────────────────────── 提权 ─────────────────────────

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(EscapeArgument)),
        };

        try
        {
            using var child = Process.Start(psi);
            child?.WaitForExit();
            return true;
        }
        catch (Exception)
        {
            // 用户点了“否”，或 UAC 不可用
            return false;
        }
    }

    private static string EscapeArgument(string arg) =>
        arg.Length > 0 && !arg.Contains(' ') && !arg.Contains('"') ? arg : $"\"{arg.Replace("\"", "\\\"")}\"";
}
