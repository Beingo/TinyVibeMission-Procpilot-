# ProcPilot — Windows 进程优先级 / 冻结管理器

> 记住你对每个进程的设定，进程每次启动时自动应用；同时把自身开销压到"后台接近零"。
> C# / WPF / .NET 8，单文件自包含，可脱离 .NET 运行时直接运行。

版本：**1.1.0**

---

## 一、功能

### 1. 进程列表与状态
- 自动列出**所有非系统进程**（Session 0 的系统服务不显示，避免误操作）。
- 可对进程施加四种状态：**低于正常** / **低于正常 · 虚拟内存** / **冻结** / **恢复正常**，
  可任意互相切换；当前状态用行底色 + 状态列 + 按钮高亮三处指示。
- **可操作性过滤**：首次启动预热 5s 内全部可操作；之后只有满足任一条件的进程可操作（亮显）：
  最近 5s 内 CPU 持续 >1%、内存 >50MB、或用户已经对它设过状态；其余低占用进程灰色显示、不可选中，
  防止误点。可操作进程永远排在灰色进程之上，组内按内存从大到小。

### 2. 进程记忆（自动规则）
- 用户给某进程设好状态后**自动记住**（可在设置里关），也可用「记住 → 自动化」/ 右键菜单显式记住、忘记。
- 该进程**以后每次启动都会被自动设置成记住的状态**；列表「自动」列显示已有规则。
- 规则按进程名（去掉 `.exe`、忽略大小写）匹配，保存在本机 `%APPDATA%\ProcPilot\rules.json`。

### 3. 开机自启动 + 随机延迟
- 「开机自启动」写当前用户启动项（`HKCU\...\Run`，不需要管理员）；可开启**随机延迟 1~N 秒**启动，
  错开开机瞬间的资源竞争；可**静默驻留托盘**不弹主窗口。

### 4. 托盘与关于
- 关闭窗口 = 最小化到托盘；托盘菜单：显示主界面 / 设置 / 关于 / 退出。
- **关于**页面：版本、构建时间、运行环境、权限、进程事件源、配置目录，以及
  「打开配置目录」「复制诊断信息」按钮。

### 5. UI
- 圆角卡片风格，跟随 Windows 深浅色主题；列表开启 UI 虚拟化。

---

## 二、设计思路与方法

### 2.1 目标与矛盾

这个工具的需求天然互相拉扯：

| 想要 | 冲突 |
|---|---|
| 一直盯着全系统进程，随时能对任意进程下手 | 天性要高频采样，采样越勤开销越大 |
| 常驻托盘、后台待命（规则随时生效） | 后台不该有任何轮询 |
| 记住规则、进程一启动就生效 | 发现"新进程"通常靠轮询 |
| 操作要立刻见效，且能任意来回切换状态 | 优先级/挂起/工作集操作都是不可幂等的原生调用 |

所以核心思路是：**把"观察"降到一个系统调用、把"发现"换成系统推送、把"操作"做成幂等**。

### 2.2 分层结构

```
┌──────────────────────────────────────────────────────────────┐
│ UI 层      MainWindow / SettingsWindow / AboutWindow (WPF)    │
│            只做展示与交互，所有跨线程回调都 marshal 回 UI 线程      │
├──────────────────────────────────────────────────────────────┤
│ 编排层     ProcessMonitor（采样+过滤+排序）                     │
│            RuleEngine（启动事件 → 命中规则 → 应用 → 复核）        │
├──────────────────────────────────────────────────────────────┤
│ 服务层     SystemProcessSnapshotSource（一次系统调用拿全部进程）    │
│            IProcessWatcher → EtwProcessWatcher / Polling...   │
│            ProcessController（三态机，幂等）                     │
│            RuleStore / SettingsStore / JsonFile（持久化）        │
├──────────────────────────────────────────────────────────────┤
│ 原生层     NativeMethods：OpenProcess / NtSuspendProcess /      │
│            NtQuerySystemInformation / ETW（StartTrace…）        │
└──────────────────────────────────────────────────────────────┘
```

UI 层不知道任何原生细节；编排层只依赖接口；服务层各自可独立自测（`selftest` 工程直接引用它们）。

### 2.3 采集：一次系统调用代替逐进程开句柄

**问题**：`Process.GetProcesses()` 再逐进程读 `SessionId` / `TotalProcessorTime` / `WorkingSet64`，
每个进程要打开约 3 次句柄；400 个进程就是 1200 次 `OpenProcess` + 数百个 `Process` 对象与其终结器。

**做法**：直接调用 `NtQuerySystemInformation(SystemProcessInformation)`，一次拿回全部进程的
PID / 映像名 / 会话 ID / 工作组大小 / 内核+用户 CPU 时间 / 线程数，缓冲区长期复用。

- 结构体字段偏移在代码中集中定义（`NativeMethods.SysProcInfo`），并由自测程序与 `Process` API
  **逐进程逐字段比对**（名字/工作集/会话/CPU 时间，实测 0 偏差）保证正确；
- 内核返回的映像名带 `.exe`，统一去掉以对齐 `Process.ProcessName`；
- PID 复用、缓冲区不足（`STATUS_INFO_LENGTH_MISMATCH`）都有明确处理分支。

**收益**：一轮采样从 ~14ms/1200 次开句柄降到 ~7ms/0 次开句柄，且没有句柄泄漏风险。

### 2.4 发现：用 ETW 事件代替轮询（为什么不能更简单）

**问题**：想在"某个进程刚启动"的瞬间应用规则，就需要一个"新进程"信号。

查过的方案与结论：

| 方案 | 结论 |
|---|---|
| 窗口消息广播（类似设备变更的 `WM_DEVICECHANGE`） | Windows **没有**进程创建/退出的桌面广播消息 |
| WMI `__InstanceCreationEvent` / `Win32_ProcessStartTrace` | 伪事件：WMI 服务自己在后台每隔 1s 轮询内核后合成，成本转移到 `WmiPrvSE.exe` |
| `NtQuerySystemInformation` 定时比对 | 本质就是轮询 |
| **ETW `Microsoft-Windows-Kernel-Process`** | **真推送**：内核在进程创建/退出时写 ETW 缓冲，消费者收事件，空闲零 CPU |

**做法**：起一个私有实时 ETW 会话，`EnableTraceEx2` 打开该提供者的 process 关键字（0x10），
`OpenTrace + ProcessTrace` 在独立线程收事件；事件里取出 PID 与映像路径后，直接交给规则引擎。

实现上有几个必须踩对的点（都已固化在代码注释里）：
- `EVENT_HEADER.ProcessId` 不是"新进程 PID"（实测是创建者语义），真正的 PID 在 **payload 第一个 uint32**；
- 映像完整路径是 payload 里**第一段以 `.exe` 结尾的 UTF-16 字符串**；
- 会话名带进程号，启动前先 `ControlTrace(STOP)` 清理上次异常退出的残留会话；
- `EVENT_TRACE_PROPERTIES` 缓冲区必须**先初始化再传给任何 API**；
- 传给 `OpenTrace` 的会话名字符串要**在整个监听期保持有效**（否则 `ProcessTrace` 可能读到已释放内存）。

**降级**：ETW 建立失败（非管理员、会话被占用等）时自动切到 `PollingProcessWatcher`——
2s 一轮、只读名字集合、每轮仅一次系统调用、0 个句柄，界面上会显示当前用的是哪种事件源。

### 2.5 采样强度分档：后台归零

| 场景 | 采样 |
|---|---|
| 窗口在前台 | 1s（数据要"活"） |
| 窗口可见但没聚焦 | 2s（可配置为暂停） |
| 隐藏到托盘 / 最小化 | **完全停止**，只等 ETW 事件 |

只有一个定时器，切档只改周期；后台时"暂停"是 `Change(Infinite, Infinite)`，不产生任何回调。
实测：托盘隐藏后 15s 内进程 CPU 时间为 **0.000s**。

### 2.6 操作：三态机 + "先复位再应用"保证幂等

`ProcessController.ApplyState(pid, target)` 的约定是：**无论进程当前处于什么状态，调用后一定处于 target**。

实现方式：每次先 `NtResumeProcess` + `SetPriorityClass(NORMAL)` 回到基线，再按 target 施加动作
（降优先级 / 清空工作集 / 挂起）。这样：
- 重复点击同一按钮无副作用；
- 状态之间任意跳转不需要"撤销上一步"的复杂逻辑；
- 冻结与解冻不会因为挂起计数累加而解不开。

代价是"恢复正常"时也会走一遍复位（本来就是复位，无额外成本）。

**退出保护**：进程退出前 `RestoreAll()` 把所有受控进程解冻并恢复 Normal，避免留下冻结的窗口。

### 2.7 规则：持久化、匹配与复核

- **持久化**：`%APPDATA%\ProcPilot\rules.json`，写入用"临时文件 + 原子替换"，读取对损坏文件做
  `.bad-<时间>` 备份后回退为空规则集——配置文件坏掉不该让程序起不来。
- **匹配**：按进程名（去 `.exe`、忽略大小写）。名字比完整路径稳定（同一程序可能装在不同路径，
  而同一名字的不同程序共用规则是用户可接受的妥协）；规则里额外记录 `ImagePath` 仅用于展示与排查。
- **应用时机**：进程启动事件到达后立刻应用。**2.5s 后复核**一次优先级类——
  有些程序（游戏启动器、自带服务端的客户端）会在自己的初始化阶段把优先级改回去；
  若发现被改回、且用户没有手动干预，就再应用一次。
- **冻结类规则不做复核**：`NtSuspendProcess` 会累加挂起计数，重复调用会导致进程解不开。
- **启动补应用**：ProcPilot 自身启动时（可配置）对已在运行且命中规则的进程补应用一次，并错峰 15ms。

### 2.8 UI 性能：少做事比做快事更重要

- 开启 `VirtualizingPanel` 虚拟化（回收模式），几百行也只实例化可见部分；
- 用"排序签名"（可操作性 + 内存按 4MB 分桶）判断是否需要 `CollectionView.Refresh()`——
  内存只是小幅抖动时完全不重排；
- 列表项模型实现 `INotifyPropertyChanged`，属性值没变就不发通知；CPU/内存文本用字符串比较避免无谓刷新；
- 快照对象是 `readonly struct`，每秒几百条不产生 GC 压力。

### 2.9 线程模型与线程安全

- ETW 回调线程：只做字段解析与事件派发，绝不阻塞（规则应用扔到线程池）。
- 线程池：规则引擎在后台应用状态；应用完成后通过 `Dispatcher.BeginInvoke` 通知 UI。
- 采样定时器线程：`Collect()` 全程持锁；`Dispose()` 持同一把锁释放非托管缓冲区，避免"边读边释放"。
- 锁顺序：先 UI 线程 → 再监视器锁 → 再控制器锁；控制器事件在**释放锁之后**触发，不会形成环。

### 2.10 可诊断性

- `DiagLog`：默认**不写**常规日志（省 IO），只记录启动与异常；设置里打开（或设环境变量
  `PROCIPILOT_DIAG=1`）后记录每轮采样耗时、规则应用、ETW 会话状态；文件 2MB 自动轮转。
- `selftest` 工程：不依赖 GUI 就能验证 6 类关键行为（见第五节），其中 `ui` 模式会把设置窗口离屏渲染成 PNG，
  用来检查 XAML/绑定有没有运行时错误。

### 2.11 隐私

- 程序**不联网、不上报任何数据**；ETW 只订阅本机内核进程事件，不采集命令行以外的内容，也不落盘。
- 所有配置都在本机 `%APPDATA%\ProcPilot\`（`settings.json` / `rules.json` / 诊断日志），删目录即恢复出厂。
- 仓库与文档里不包含任何个人路径、机器名、内网地址；构建脚本用相对路径与 `dotnet`（PATH）而不是绝对路径。

---

## 三、目录结构

```
ProcPilot/
├─ App.xaml(.cs)            应用入口：单实例、主题、自启动参数、随机延迟
├─ MainWindow.xaml(.cs)     主界面：进程列表 / 状态按钮 / 记住 / 设置 / 关于 / 托盘
├─ SettingsWindow.xaml(.cs) 设置：规则管理、开机启动、后台采样、诊断
├─ AboutWindow.xaml(.cs)    关于：版本、运行环境、设计要点
├─ Models/                  ProcessInfo（UI 行）/ ProcessSnapshot（采样值）/ ProcessRule / AppSettings
├─ Services/
│  ├─ SystemProcessSnapshot.cs   一次 NtQuerySystemInformation 采集全部进程
│  ├─ ProcessWatcherContracts.cs 事件源接口与事件参数
│  ├─ EtwProcessWatcher.cs       ETW 实时消费者（含 payload 解析）
│  ├─ PollingProcessWatcher.cs   降级轮询
│  ├─ ProcessMonitor.cs          采样 / 过滤 / 排序 / 分档
│  ├─ RuleEngine.cs              规则引擎（应用 + 复核）
│  ├─ ProcessController.cs       三态机（幂等）
│  ├─ RuleStore.cs / SettingsStore.cs / JsonFile.cs / AppPaths.cs   持久化
│  ├─ AutoStartManager.cs        开机自启
│  ├─ AppInfo.cs                 版本、运行环境
│  ├─ DiagLog.cs / ThemeManager.cs / IconProvider.cs / NativeMethods.cs
├─ Themes/                  浅色 / 深色配色与控件样式
├─ scripts/                 build.ps1
├─ LICENSE                  MIT 许可（允许商用，需保留版权与许可声明）
└─ selftest/                无 GUI 自测工程（snapshot/perf/rules/etw/etw2/ui/all）
```

---

## 四、构建与发布

需要 **.NET SDK 8**（`dotnet` 在 PATH 中，或给脚本传 `-Dotnet <路径>`）。

```powershell
# 调试构建
dotnet build -c Debug

# 一键发布单文件（产物在 release\）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

产物：`release\ProcPilot.exe`（单文件自包含）。

---

## 五、自测（不开 GUI）

```powershell
dotnet run --project selftest -- snapshot   # 校验 NtQuerySystemInformation 解析与 Process API 一致
dotnet run --project selftest -- perf       # 新旧采样实现耗时对比
dotnet run --project selftest -- rules      # 规则读写往返 / 大小写归一 / 删除
dotnet run --project selftest -- etw        # ETW 能否收到进程启动事件（需管理员）
dotnet run --project selftest -- etw2       # ETW 事件结构细查（打印 payload）
dotnet run --project selftest -- ui         # 设置窗口离屏渲染成 PNG（不打扰桌面）
dotnet run --project selftest -- all        # 全部
```

---

## 六、已知限制

- **必须管理员权限**（`requireAdministrator`）：修改他人进程的优先级/挂起需要。拒绝 UAC 时列表近乎为空。
- **自动规则按进程名匹配**：同名不同路径的程序会共用一条规则。
- **冻结类规则每次启动只挂起一次**（避免挂起计数累加导致解不开）。
- **退出即恢复**：ProcPilot 正常退出时会把受控进程恢复到正常状态（安全优先）。
- **ETW 需要管理员**：非管理员时自动降级为 2s 轮询，界面上会显示"事件源：轮询兜底"。

---

## 七、变更记录

### 1.2.0
- 新增「关于」页面（版本 / 运行环境 / 权限 / 事件源 / 设计要点 / 诊断信息复制）；
- 明确不往用户机器安装任何根证书（移除早期的证书信任引导）；
- 补 MIT 许可；README 重写为设计文档并清除本机路径/机器名；自测工程并入仓库。

### 1.1.0
- 新增进程记忆（自动规则）、开机自启动 + 随机延迟、关于页面；
- 采样改为单次系统调用；发现新进程改为 ETW 事件驱动；后台采样归零；
- UI 虚拟化与刷新策略优化；诊断日志默认关闭并改为缓冲写；
- 自测工程并入仓库（`selftest/`）。

### 1.0.0
- 进程列表 + 四种状态切换 + 托盘 + 深浅色主题。

---

## 八、许可与署名

本项目以 **MIT License** 发布（见 `LICENSE`）：

- **允许**：商业使用、修改、再分发、私有化部署；
- **要求**：保留原版权声明与许可声明（即"注明作者与出处"）。再分发或产品里引用时，请保留
  `LICENSE` 文件或版权声明，并注明来源为本项目（ProcPilot）。
- **不提供担保**：按现状提供，作者不对使用后果负责。

如果需要，可以替换 `LICENSE` 里的 `ProcPilot Authors` 为你自己的署名。
