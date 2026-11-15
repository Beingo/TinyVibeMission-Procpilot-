using System.Diagnostics;
using System.IO;
using System.Windows;
using ProcPilot.Models;
using ProcPilot.Services;

namespace ProcPilot.SelfTest;

/// <summary>
/// ProcPilot 自测（无需 GUI）：
///   dotnet run -- snapshot   校验 NtQuerySystemInformation 解析结果与 Process API 一致
///   dotnet run -- etw        校验 ETW 内核进程事件能收到（自动拉起 notepad 验证）
///   dotnet run -- perf       对比新旧采样实现的耗时/句柄开销
///   dotnet run -- rules      校验规则仓库持久化往返
///   dotnet run -- all        以上全部
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var tmp = Path.Combine(Path.GetTempPath(), "procpilot-selftest");
        Directory.CreateDirectory(tmp);
        AppPaths.UseDataDir(tmp);
        DiagLog.UseFile(Path.Combine(tmp, "selftest.log"));

        Console.WriteLine($"== ProcPilot selftest ({mode}) ==  管理员：{IsElevated()}");
        if (mode is "snapshot" or "all") CheckSnapshot();
        if (mode is "perf" or "all") CheckPerf();
        if (mode is "rules" or "all") CheckRules();
        if (mode is "etw" or "all") CheckEtw();
        if (mode is "etw2") CheckEtwVerbose();
        if (mode is "ui") CheckUi(tmp);

        Console.WriteLine(_failures == 0 ? "== 全部通过 ==" : $"== 失败 {_failures} 项 ==");
        return _failures == 0 ? 0 : 1;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static void Ok(string msg) => Console.WriteLine("  [OK] " + msg);
    private static void Bad(string msg) { _failures++; Console.WriteLine("  [FAIL] " + msg); }

    // ---------------- 1. 快照解析校验 ----------------
    private static void CheckSnapshot()
    {
        Console.WriteLine("-- 快照解析校验（NtQuerySystemInformation vs Process API）--");
        var src = new SystemProcessSnapshotSource();
        var entries = src.Collect();
        var byPid = new Dictionary<int, ProcEntry>();
        foreach (var e in entries) byPid[e.Pid] = e;
        Console.WriteLine($"  系统返回 {entries.Count} 个有名字的进程，采样耗时 {src.LastCollectMicros}µs");

        int checkedCount = 0, nameMiss = 0, wsMiss = 0, sessMiss = 0, cpuMiss = 0, notFound = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4) continue;
                if (!byPid.TryGetValue(p.Id, out var e)) { notFound++; continue; }
                checkedCount++;

                string apiName = p.ProcessName;
                if (!string.Equals(apiName, e.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (nameMiss++ < 5) Console.WriteLine($"    名字不一致 pid={p.Id}: api={apiName} nt={e.Name}");
                }
                if (Math.Abs(p.WorkingSet64 - e.WorkingSet) > 4 * 1024 * 1024)
                {
                    if (wsMiss++ < 5) Console.WriteLine($"    工作集偏差 pid={p.Id} {apiName}: api={p.WorkingSet64 / 1048576}MB nt={e.WorkingSet / 1048576}MB");
                }
                try
                {
                    if (p.SessionId != e.SessionId && sessMiss++ < 5)
                        Console.WriteLine($"    会话不一致 pid={p.Id} {apiName}: api={p.SessionId} nt={e.SessionId}");
                }
                catch { }
                try
                {
                    var apiCpu = p.TotalProcessorTime.TotalSeconds;
                    var ntCpu = e.CpuTime.TotalSeconds;
                    if (Math.Abs(apiCpu - ntCpu) > 0.5 && cpuMiss++ < 5)
                        Console.WriteLine($"    CPU 时间偏差 pid={p.Id} {apiName}: api={apiCpu:F2}s nt={ntCpu:F2}s");
                }
                catch { }
            }
            catch { }
            finally { p.Dispose(); }
        }

        Console.WriteLine($"  对比 {checkedCount} 个进程：名字不符 {nameMiss}，工作集偏差 {wsMiss}，" +
                          $"会话不符 {sessMiss}，CPU时间偏差 {cpuMiss}，未在快照中 {notFound}");
        if (checkedCount < 20) Bad("对比样本太少，可能权限不足");
        else if (nameMiss == 0 && wsMiss == 0 && sessMiss == 0 && cpuMiss == 0) Ok("快照字段与 Process API 完全一致");
        else Bad("快照字段与 Process API 存在偏差，需检查 StructureOffset");
    }

    // ---------------- 2. 新旧采样开销对比 ----------------
    private static void CheckPerf()
    {
        Console.WriteLine("-- 采样开销对比 --");
        const int rounds = 30;

        // 旧实现：Process.GetProcesses() + 逐进程打开句柄
        var oldTimes = new List<double>();
        int oldPidCount = 0;
        for (int i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            int count = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    _ = p.Id;
                    _ = p.ProcessName;
                    _ = p.SessionId;
                    _ = p.WorkingSet64;
                    _ = p.TotalProcessorTime;
                    count++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            sw.Stop();
            oldTimes.Add(sw.Elapsed.TotalMilliseconds);
            oldPidCount = count;
            Thread.Sleep(15);
        }

        // 新实现：一次 NtQuerySystemInformation
        var src = new SystemProcessSnapshotSource();
        src.Collect();
        var newTimes = new List<double>();
        int newPidCount = 0;
        for (int i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            var entries = src.Collect();
            sw.Stop();
            newTimes.Add(sw.Elapsed.TotalMilliseconds);
            newPidCount = entries.Count;
        }

        // 只测系统调用本身（缓冲区已足够大，不含解析/字符串分配）
        var rawTimes = new List<double>();
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(4 * 1024 * 1024);
        int rawLen = 0;
        try
        {
            for (int i = 0; i < rounds; i++)
            {
                var sw = Stopwatch.StartNew();
                NativeMethods.NtQuerySystemInformation(5, buf, 4 * 1024 * 1024, out rawLen);
                sw.Stop();
                rawTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }

        double oldAvg = oldTimes.Average(), newAvg = newTimes.Average();
        Console.WriteLine($"  仅系统调用（缓冲区共用）：平均 {rawTimes.Average():F2}ms（返回长度 {rawLen / 1024}KB）");
        Console.WriteLine($"  旧实现（Process API 逐进程）：平均 {oldAvg:F2}ms  最小 {oldTimes.Min():F2}ms  进程 {oldPidCount} 个");
        Console.WriteLine($"  新实现（单次系统调用）    ：平均 {newAvg:F2}ms  最小 {newTimes.Min():F2}ms  进程 {newPidCount} 个");
        Console.WriteLine($"  提速 {oldAvg / Math.Max(0.0001, newAvg):F1}x；每次采样少打开约 {oldPidCount * 3} 个进程句柄");
        if (newAvg < oldAvg) Ok("新实现更快");
        else Bad("新实现没有更快，需检查");
    }

    // ---------------- 3. 规则持久化 ----------------
    private static void CheckRules()
    {
        Console.WriteLine("-- 规则仓库持久化 --");
        var store = new RuleStore();
        store.Load();
        int before = store.Count;
        store.Upsert("Notepad.exe", ProcessStateKind.BelowNormal, @"C:\Windows\notepad.exe");
        store.Upsert("chrome", ProcessStateKind.Frozen);
        store.Upsert("chrome", ProcessStateKind.Frozen); // 幂等

        var reloaded = new RuleStore();
        reloaded.Load();
        bool hasNotepad = reloaded.TryGet("notepad", out var r1) && r1.State == ProcessStateKind.BelowNormal;
        bool hasChrome = reloaded.TryGet("CHROME", out var r2) && r2.State == ProcessStateKind.Frozen;
        bool caseInsensitive = reloaded.TryGet("Chrome.EXE", out _);

        reloaded.Remove("chrome");
        reloaded.Remove("notepad");
        var third = new RuleStore();
        third.Load();
        bool cleaned = !third.TryGet("chrome", out _) && !third.TryGet("notepad", out _);

        Console.WriteLine($"  起始 {before} 条 → 写入 2 条 → 重新加载后 notepad={hasNotepad} chrome={hasChrome} 忽略大小写={caseInsensitive} 删除后={cleaned}");
        if (hasNotepad && hasChrome && caseInsensitive && cleaned) Ok("规则读写/大小写归一/删除均正常");
        else Bad("规则仓库行为不符合预期");
    }

    // ---------------- 4. ETW 事件 ----------------
    private static void CheckEtw()
    {
        Console.WriteLine("-- ETW 内核进程事件 --");
        using var watcher = new EtwProcessWatcher();
        int startedCount = 0;
        var startedNames = new List<string>();
        var rawSeen = new List<string>();

        watcher.ProcessStarted += e =>
        {
            Interlocked.Increment(ref startedCount);
            string name = e.Name ?? "";
            if (string.IsNullOrEmpty(name))
            {
                string path = NativeMethods.TryGetImagePath(e.Pid);
                name = Path.GetFileNameWithoutExtension(path);
            }
            lock (startedNames) startedNames.Add($"{e.Pid}:{name}");
        };
        watcher.RawEvent += (id, task, opcode, pid) =>
        {
            lock (rawSeen)
            {
                if (rawSeen.Count < 12) rawSeen.Add($"id={id},task={task},opcode={opcode},pid={pid}");
            }
        };

        if (!watcher.Start())
        {
            Bad($"ETW 启动失败：{watcher.LastError}（非管理员时属于预期，运行时会自动降级轮询）");
            return;
        }
        Ok("ETW 会话已建立：" + watcher.Mode);

        // 拉起 3 个 notepad 验证事件
        var launched = new List<Process>();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
                launched.Add(Process.Start(psi)!);
                Thread.Sleep(400);
            }
            catch (Exception ex) { Console.WriteLine("    拉起 notepad 失败：" + ex.Message); }
        }

        Thread.Sleep(1500);
        foreach (var p in launched)
        {
            try
            {
                if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); }
            }
            catch { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(500);

        int notepadEvents;
        lock (startedNames) notepadEvents = startedNames.Count(s => s.Contains("notepad", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  收到进程启动事件 {startedCount} 个，其中 notepad {notepadEvents} 个");
        lock (startedNames) Console.WriteLine("    样本：" + string.Join(", ", startedNames.Take(10)));
        lock (rawSeen) Console.WriteLine("    原始事件：" + string.Join(" | ", rawSeen));

        if (notepadEvents >= 2) Ok("ETW 能收到进程启动事件（Task/Opcode 解析正确）");
        else Bad("没有收到 notepad 启动事件，Task/Opcode 或会话配置需要修正");

        watcher.Dispose();
    }

    // ---------------- 5. ETW 事件结构细查 ----------------
    private static void CheckEtwVerbose()
    {
        Console.WriteLine("-- ETW 事件结构细查 --");
        using var watcher = new EtwProcessWatcher();
        int n = 0;
        watcher.DetailedEvent += d =>
        {
            int i = Interlocked.Increment(ref n);
            Console.WriteLine($"  #{i} id={d.Id} task={d.Task} op={d.Opcode} ver={d.Version} ch={d.Channel} " +
                              $"lvl={d.Level} kw=0x{d.Keyword:X} pid={d.ProcessId} tid={d.ThreadId} :: {d.UserDataPreview()}");
        };
        if (!watcher.Start())
        {
            Bad("ETW 启动失败：" + watcher.LastError);
            return;
        }
        Ok("会话已建立，先静置 1s 观察是否有多余事件…");
        Thread.Sleep(1000);
        Console.WriteLine($"  静置期间事件数：{n}");

        Console.WriteLine("  拉起 2 个普通进程（cmd /c ping），记录预期 PID…");
        var expected = new List<int>();
        for (int i = 0; i < 2; i++)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true };
                var p = Process.Start(psi);
                if (p != null) { expected.Add(p.Id); Console.WriteLine($"    预期 PID={p.Id}"); }
            }
            catch (Exception ex) { Console.WriteLine("    启动失败：" + ex.Message); }
            Thread.Sleep(300);
        }
        Thread.Sleep(1200);

        Console.WriteLine("  拉起 notepad（UseShellExecute=false）…");
        Process? note = null;
        try
        {
            note = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false });
            Console.WriteLine($"    Process.Start 返回 PID={note?.Id}");
        }
        catch (Exception ex) { Console.WriteLine("    启动失败：" + ex.Message); }

        Thread.Sleep(2500);
        Console.WriteLine($"  notepad 运行后总事件数：{n}");
        Console.WriteLine("  预期 cmd PID 列表：" + string.Join(",", expected));
        try { if (note is { HasExited: false }) { note.Kill(); note.WaitForExit(2000); } } catch { }
        try { note?.Dispose(); } catch { }
        Thread.Sleep(800);
        Console.WriteLine($"  最终事件数：{n}");
    }

    // ---------------- 6. 界面离屏渲染（不打扰桌面） ----------------
    private static void CheckUi(string tmpDir)
    {
        Console.WriteLine("-- 界面离屏渲染 --");
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ProcPilot;component/Themes/Colors.Light.xaml"),
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ProcPilot;component/Themes/Styles.xaml"),
        });

        var rules = new RuleStore();
        rules.Load();
        rules.Upsert("chrome", ProcessStateKind.BelowNormal, @"C:\Program Files\Google\Chrome\chrome.exe");
        rules.Upsert("notepad", ProcessStateKind.Frozen);
        rules.Upsert("potplayermini64", ProcessStateKind.BelowNormalTrimmed);

        var w = new SettingsWindow(rules, SettingsStore.Current, "ETW 内核进程事件（系统推送，零轮询）")
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        w.Show();
        Pump();
        Pump();

        var content = (System.Windows.FrameworkElement)w.Content;
        int width = Math.Max(400, (int)content.ActualWidth);
        int height = Math.Max(300, (int)content.ActualHeight);
        Console.WriteLine($"  设置窗口尺寸 {width}x{height}");

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        var outPath = Path.Combine(tmpDir, "settings-window.png");
        using (var fs = File.Create(outPath)) enc.Save(fs);
        w.Close();
        Console.WriteLine("  已渲染：" + outPath + "  " + new FileInfo(outPath).Length + " bytes");
        Ok("设置窗口渲染成功（XAML 资源/绑定无运行时错误）");

        // 关于窗口
        var about = new ProcPilot.AboutWindow("ETW 内核进程事件（系统推送，零轮询）")
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        about.Show();
        Pump();
        Pump();
        var aboutContent = (System.Windows.FrameworkElement)about.Content;
        int aw = Math.Max(400, (int)aboutContent.ActualWidth);
        int ah = Math.Max(300, (int)aboutContent.ActualHeight);
        var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(aw, ah, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb2.Render(aboutContent);
        var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb2));
        var aboutPath = Path.Combine(tmpDir, "about-window.png");
        using (var fs2 = File.Create(aboutPath)) enc2.Save(fs2);
        about.Close();
        Console.WriteLine($"  关于窗口 {aw}x{ah} 已渲染：{aboutPath}");
        app.Shutdown();
        Ok("关于窗口渲染成功");
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
