# PclMemOpt

PCL-CE「百宝箱 → 内存优化」功能的独立命令行版本。

> ### ⚠️ 与 PCL / PCL-CE 无关
>
> 本项目是**第三方独立作品**，未经 PCL-CE 维护者或龙腾猫跃审核、认可或背书，
> 与两者**均无隶属关系**。
>
> 内存操作部分（`NtInterop.cs` / `MemSwap.cs`）逐行取自 **PCL-CE v2.15.0-beta.2**（Apache-2.0），
> 因此本项目按 **Apache-2.0** 分发，并附 [NOTICE](NOTICE) 说明来源与改动。
> PCL2 的《分发有限许可》与《存储库合理使用指南》原文收录于 [PCL-LICENCE.md](PCL-LICENCE.md)。
> 鸣谢：龙腾猫跃（[爱发电](https://afdian.com/a/LTCat)）· [PCL-CE](https://github.com/PCL-Community/PCL-CE)。

原功能已在 PCL-CE `v2.15.0-beta.3` 中被移除（PR #3067），本工具把那套逻辑拿出来做成一个独立 CLI，
免费获得原有的效果，且不依赖启动器版本。

## 它做了什么

底层就是原实现用的那 7 个操作，全部通过**未文档化的** `NtSetSystemInformation` 完成：

| 操作 | 说明 |
|---|---|
| 清空工作集 | 把各进程的物理页换出到页面文件 |
| 刷新文件缓存 | `SystemFileCacheInformationEx` |
| 清空已修改页列表 | 强制把脏页写回磁盘 |
| 清空待机列表 | **效果最明显**，"占用掉几个 G"主要靠它 |
| 清空低优先级待机列表 | 同上，范围更小 |
| 注册表协调 | `SystemRegistryReconciliationInformation` |
| 合并物理内存 | `SystemCombinePhysicalMemoryInformation` |

前置条件：需要 `SeProfileSingleProcessPrivilege` + `SeIncreaseQuotaPrivilege`，**必须管理员权限**。
非管理员下这些调用会静默失败（表现为"只优化了几百字节"），本工具会在提权失败时明确报错并中止。

## 用法

```bat
:: 全部执行（默认）
PclMemOpt.exe

:: 只清待机列表 —— 日常最实用的一项
PclMemOpt.exe -s

:: 清待机列表 + 清工作集
PclMemOpt.exe -s -w

:: 挂在后台，每 300 秒来一次
PclMemOpt.exe --loop 300

:: 看看会执行什么，不动手
PclMemOpt.exe --dry-run

:: 退出码示例
PclMemOpt.exe -s && echo 成功
```

不指定操作时等价于 `--all`。首次运行会弹 UAC 请求提权（`--no-elevate` 可跳过）。

### 参数

```
操作选择:
  -w, --working-set     清空工作集
  -f, --file-cache      刷新系统文件缓存
  -m, --modified-list   清空已修改页列表
  -s, --standby         清空待机列表
      --standby-low     清空低优先级待机列表
  -r, --registry        注册表协调
  -c, --combine         合并物理内存
      --all             全部（默认）

运行方式:
      --loop <秒>       循环执行
      --dry-run         只列出操作
      --no-elevate      不提权
  -q, --quiet           安静模式
  -h, --help            帮助

退出码: 0 成功 / 2 参数错误 / 3 特权失败 / 4 部分操作失败 / 5 提权被拒
```

典型输出（Windows 10.0.26300 / 16 GB 内存实测）：

```
优化前  可用物理内存 5.33 GB / 15.85 GB (占用 66%)    页面文件已用 17.34 GB
  [1/7] 清空工作集                 OK       1099 ms
  [2/7] 刷新文件缓存                OK          0 ms
  [3/7] 清空已修改页列表              OK       6492 ms
  [4/7] 清空待机列表                OK       1440 ms
  [5/7] 清空低优先级待机列表            OK          0 ms
  [6/7] 注册表协调                 OK        113 ms
  [7/7] 合并物理内存                OK        497 ms

优化后  可用物理内存 10.44 GB / 15.85 GB (占用 34%)    页面文件已用 17.54 GB

结果: 可用物理内存 +5.11 GB    耗时 9.65 s    成功 7/7 项
代价: 页面文件多占用 210.85 MB
```

连续执行第二遍的边际效果（同一台机器，紧接着又跑一次）：

| 轮次 | 释放的可用物理内存 | 页面文件增加 | 耗时 |
|---|---|---|---|
| 第 1 次 | +5.11 GB | +210.85 MB | 9.65 s |
| 第 2 次 | +2.65 GB | +20.46 MB | 3.50 s |

## 结果怎么看

可用物理内存确实大幅上升，但**这不等于内存压力真的降低了**。看上面那组数据：
释放了 5.11 GB，页面文件只多了 211 MB —— 说明被回收的主要是**待机列表里的干净缓存页**
（已经被丢弃，不需要写盘），只有 211 MB 是需要写回的脏页。

所以真实的代价是：

- **文件缓存被清空**，之后重新读盘会变慢（游戏刚加载完切出去再切回来，卡顿会更明显）；
- **脏页写盘**带来的 SSD 写入量 —— 这次是 211 MB，跑得越频繁、系统越活跃，这个数越大；
- 待机列表被清掉后，系统需要时会重新从硬盘把数据读回来，**净效果可能是负的**。

这功能适合的场景很窄：启动游戏前手动来一次，把被缓存占满的物理内存腾出来给游戏。
不适合每隔几分钟自动跑一次 —— 那只会让系统反复丢弃又重建缓存。

换句话说，"占用从 5.5G 降到 3.6G"是账面数字。真正需要的是更多物理内存，或者关掉后台程序。
Mem Reduct 这类工具做的事完全一样，本工具只是把 PCL-CE 的版本单独拎出来用。

## 构建

需要 .NET 10 SDK：

```bat
dotnet publish -c Release -p:PublishSingleFile=true -p:SelfContained=false
```

产物在 `bin/Release/net10.0-windows/win-x64/publish/PclMemOpt.exe`，需目标机器安装 .NET 10 运行时。
想免运行时依赖，把 `-p:SelfContained=false` 换成 `-p:SelfContained=true`（体积约 15 MB）。

## 与 PCL-CE 原实现的差异

| 项 | PCL-CE 原版 | 本工具 |
|---|---|---|
| 提权方式 | `PromoteService` 单开提权子进程 | 进程自我重启（`runas`） |
| 触发方式 | 百宝箱按钮 / 启动前自动执行 | 命令行参数 |
| 后台常驻 | 无 | `--loop` |
| 失败反馈 | 弹窗 | 逐项计时 + NTSTATUS + 退出码 |

内存操作部分（`NtInterop.cs` / `MemSwap.cs`）与 PCL-CE `v2.15.0-beta.2` 的
`NtInterop.cs` / `MemSwapWorks.cs` / `MemSwapService.cs` 逐行等价，枚举值与结构体布局全部照抄。

## 保留的维护提示

原作者移除它的两条理由依然成立，用之前心里有数：

1. `SystemMemoryListInformation` 等枚举值**从未被微软文档化**，属于逆向结果，
   Windows 大版本更新后可能变更，届时工具会直接失败（好处是它会明确报错，不会静默做错事）；
2. 它把内存换到页面文件，对 SSD 有额外写入量。

## 协议与署名

本项目按 **Apache License 2.0** 分发（见 [LICENSE](LICENSE)）。

上游来源与各自的义务：

| 上游 | 协议 | 本项目如何履行 |
|---|---|---|
| [PCL-CE](https://github.com/PCL-Community/PCL-CE) | Apache-2.0 | 保留协议全文；附 [NOTICE](NOTICE) 指明派生文件、声明改动；不使用其商标暗示背书 |
| [PCL2（Plain Craft Launcher）](https://github.com/Meloong-Git/PCL) | 《PCL 分发有限许可》+《合理使用指南》 | 全文收录于 [PCL-LICENCE.md](PCL-LICENCE.md)；署名龙腾猫跃并给出赞助链接；明确不与 PCL 混淆 |

### 为什么按 Apache-2.0 而不是 MIT

`NtInterop.cs` 与 `MemSwap.cs` 与 PCL-CE `v2.15.0-beta.2` 的同名文件**逐行等价**（枚举值、结构体布局、
调用序列全部照抄），属于 Apache-2.0 意义上的**衍生作品**，因此必须沿用 Apache-2.0 并保留 NOTICE，
不能改为更宽松的协议。

### 关于《PCL 存储库合理使用指南》的归类

按指南的定义，本项目属于**轻度使用**：虽然参考了较大量的内容，但产出的软件与 PCL 的实质功能
（启动/下载 Java 版 Minecraft、下载或管理 Mod 等）**不存在任何重叠** —— 本工具是一个纯命令行的
Windows 内存优化程序，没有启动器功能、没有 GUI、没有主题色、没有任何形式的赞助解锁。
因此适用轻度使用的两条要求：**给出署名**、**不得与 PCL 混淆或暗示与 PCL 有关**；
重度使用要求的「名称必须以 Plain Craft Launcher (PCL) 开头」等条款不适用。

需要说明的是，`PclMemOpt` 这一名称以 `Pcl` 开头，可能被理解为与 PCL 有关联。本项目已在
README 顶部与 NOTICE 中明确声明无隶属关系；如果希望进一步降低歧义，可以改名（例如 `WinMemOpt`），
这属于维护者自行取舍。

### 贡献与再分发

再分发时请一并保留 `LICENSE`、`NOTICE`、`PCL-LICENCE.md` 三个文件，并保留本节的署名信息。
