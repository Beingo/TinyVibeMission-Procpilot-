using System.Runtime.InteropServices;
using System.Text;

namespace ProcPilot.Services;

/// <summary>
/// ETW（Windows 事件跟踪）实时消费者：订阅 <c>Microsoft-Windows-Kernel-Process</c> 提供者的
/// 进程启动/退出事件，拿到"系统真正推过来"的通知。
///
/// 背景（需求4）：Windows 没有任何"进程列表变更广播"消息（不像 WM_DEVICECHANGE 那样的
/// 桌面广播）；WMI 的 __InstanceCreationEvent 本质也是 WMI 自己在后台轮询。
/// 用户态唯一真正事件驱动、零轮询的通知通道就是 ETW 的内核进程提供者。
///
/// 因此默认用本类监听；若 StartTrace/OpenTrace 失败（权限、会话被占用、非 Windows 等），
/// 由 <see cref="PollingProcessWatcher"/> 兜底。
/// </summary>
public sealed class EtwProcessWatcher : IProcessWatcher
{
    private const int EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const int PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const int PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const ulong WINEVENT_KEYWORD_PROCESS = 0x10;
    private const byte TRACE_LEVEL_VERBOSE = 5;

    private static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

    private readonly string _sessionName;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _traceThread;
    private IntPtr _properties;
    private IntPtr _sessionNamePtr;
    private ulong _sessionHandle;
    private ulong _traceHandle;
    private bool _started;

    /// <summary>进程启动（含 PID 与映像路径）。在 ETW 回调线程上触发，处理必须够快。</summary>
    public event Action<ProcessStartedEvent>? ProcessStarted;

    /// <summary>进程退出（参数为 PID）。</summary>
    public event Action<int>? ProcessStopped;

    /// <summary>所有收到的内核进程事件（诊断/自测用）：(eventId, task, opcode, pid)。</summary>
    public event Action<ushort, ushort, byte, int>? RawEvent;

    /// <summary>详细事件信息（仅当订阅时才分配内存，供自测排查事件结构）。</summary>
    public event Action<EventDetail>? DetailedEvent;

    public string LastError { get; private set; } = "";

    /// <summary>事件源说明（界面展示用）。</summary>
    public string Mode => "ETW 内核进程事件（系统推送，零轮询）";

    /// <summary>本实现不依赖轮询。</summary>
    public bool IsPolling => false;

    public EtwProcessWatcher()
    {
        _sessionName = $"ProcPilot-Process-{Environment.ProcessId}";
    }

    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW 的 x64 布局（偏移见注释）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EventTraceLogfile
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(16)] public long CurrentTime;
        [FieldOffset(24)] public uint BuffersRead;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(400)] public IntPtr BufferCallback;
        [FieldOffset(408)] public uint BufferSize;
        [FieldOffset(412)] public uint Filled;
        [FieldOffset(416)] public uint EventsLost;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
        [FieldOffset(432)] public uint IsKernelTrace;
        [FieldOffset(440)] public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong traceHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeywords, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(IntPtr handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    private EventRecordCallbackDelegate? _callbackDelegate; // 必须保持引用，否则被 GC 后回调崩溃

    /// <summary>启动监听。返回 false 时看 <see cref="LastError"/>。</summary>
    public bool Start()
    {
        if (_started) return true;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            LastError = "非 Windows 平台";
            return false;
        }

        int structSize = Marshal.SizeOf<EventTraceProperties>();
        int nameBytes = Encoding.Unicode.GetByteCount(_sessionName) + 2;
        int total = structSize + nameBytes;

        _properties = Marshal.AllocHGlobal(total);

        try
        {
            // 先清零 + 写入会话名，再构造 properties：
            // CleanUpExistingSession 会用这个缓冲区调 ControlTrace，未初始化的内存会导致未定义行为（曾偶发 0xC0000005）。
            Marshal.Copy(new byte[total], 0, _properties, total);
            byte[] nameBytesArr = Encoding.Unicode.GetBytes(_sessionName + "\0");
            Marshal.Copy(nameBytesArr, 0, _properties + structSize, nameBytesArr.Length);

            var props = new EventTraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)total },
                BufferSize = (uint)total,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,
                LoggerNameOffset = (uint)structSize,
            };
            Marshal.StructureToPtr(props, _properties, false);
            DiagLog.Write($"EtwProcessWatcher: properties 布局 {structSize}B, 会话名偏移 {structSize}");

            CleanUpExistingSession(); // 清掉上次异常退出残留的同名会话

            uint rc = StartTraceW(out _sessionHandle, _sessionName, _properties);
            if (rc != 0)
            {
                LastError = $"StartTrace 失败，Win32 错误 {rc}";
                return false;
            }

            var provider = KernelProcessProvider;
            uint rc2 = EnableTraceEx2(_sessionHandle, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_VERBOSE, WINEVENT_KEYWORD_PROCESS, 0, 0, IntPtr.Zero);
            if (rc2 != 0)
            {
                LastError = $"EnableTraceEx2 失败，Win32 错误 {rc2}";
                StopSession();
                return false;
            }

            _callbackDelegate = OnEventRecord;
            IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_callbackDelegate);
            // 会话名指针必须在整个监听期间保持有效（OpenTrace/ProcessTrace 可能仍引用它）
            _sessionNamePtr = Marshal.StringToHGlobalUni(_sessionName);
            {
                var logfile = new EventTraceLogfile
                {
                    LoggerName = _sessionNamePtr,
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                    EventRecordCallback = callbackPtr,
                };

                _traceHandle = OpenTraceW(ref logfile);
                if (_traceHandle == 0 || _traceHandle == ulong.MaxValue)
                {
                    LastError = $"OpenTrace 失败，Win32 错误 {Marshal.GetLastWin32Error()}";
                    StopSession();
                    return false;
                }
            }

            // ProcessTrace 阻塞直到会话关闭：放到独立后台线程
            _traceThread = new Thread(TraceLoop)
            {
                IsBackground = true,
                Name = "ProcPilot-ETW",
            };
            _traceThread.Start();
            _started = true;
            DiagLog.Write($"EtwProcessWatcher: 会话 {_sessionName} 已启动");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("EtwProcessWatcher.Start", ex);
            LastError = ex.Message;
            StopSession();
            return false;
        }
    }

    private void TraceLoop()
    {
        try
        {
            IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(handles, new IntPtr(unchecked((long)_traceHandle)));
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero); // 阻塞，直到 ControlTrace(STOP)
            }
            finally
            {
                Marshal.FreeHGlobal(handles);
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("EtwProcessWatcher.TraceLoop", ex);
        }
    }

    /// <summary>ETW 回调（ETW 自己的线程）——只做字段解析与派发，绝不做慢操作。</summary>
    private void OnEventRecord(IntPtr eventRecord)
    {
        try
        {
            if (eventRecord == IntPtr.Zero) return;

            // EVENT_HEADER(x64): Flags@4 ThreadId@8 ProcessId@12 TimeStamp@16 ProviderId@24
            //                    EventDescriptor@40(Id@40 Version@42 Channel@43 Level@44 Opcode@45 Task@46 Keyword@48)
            int headerPid = Marshal.ReadInt32(eventRecord + 12);
            ushort id = (ushort)Marshal.ReadInt16(eventRecord + 40);
            byte opcode = Marshal.ReadByte(eventRecord + 45);
            ushort task = (ushort)Marshal.ReadInt16(eventRecord + 46);

            // EVENT_RECORD: BufferContext@80 ExtendedDataCount@84 UserDataLength@86 ExtendedData@88 UserData@96
            int userDataLen = (ushort)Marshal.ReadInt16(eventRecord + 86);
            IntPtr userData = Marshal.ReadIntPtr(eventRecord + 96);

            RawEvent?.Invoke(id, task, opcode, headerPid);
            bool wantDetail = DetailedEvent != null;
            EventDetail? detail = wantDetail ? BuildDetail(eventRecord, id, task, opcode, headerPid, userDataLen, userData) : null;
            if (detail != null) DetailedEvent?.Invoke(detail);

            // Microsoft-Windows-Kernel-Process：
            //   Id=1 Opcode=1 → ProcessStart，Id=2 Opcode=2 → ProcessStop
            //   payload 头部第一个 uint32 才是【新进程 PID】（EVENT_HEADER.ProcessId 是创建者/PID 语义不可靠）
            //   payload 里第一段以 .exe 结尾的 UTF-16 串是映像完整路径
            bool isStart = id == 1 || opcode == 1;
            bool isStop = id == 2 || opcode == 2;
            if (!isStart && !isStop) return;
            if (userData == IntPtr.Zero || userDataLen < 4) return;

            int pid = Marshal.ReadInt32(userData);
            if (pid <= 0) return;

            if (isStart)
            {
                string? path = ExtractImagePath(userData, userDataLen, out string? name);
                ProcessStarted?.Invoke(new ProcessStartedEvent(pid, path, name));
            }
            else
            {
                ProcessStopped?.Invoke(pid);
            }
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("EtwProcessWatcher.OnEventRecord", ex);
        }
    }

    /// <summary>从 payload 里取映像完整路径：第一段以 .exe 结尾的 UTF-16 字符串。</summary>
    private static string? ExtractImagePath(IntPtr userData, int byteLength, out string? name)
    {
        name = null;
        int chars = byteLength / 2;
        if (chars <= 4) return null;

        var buf = new char[chars];
        Marshal.Copy(userData, buf, 0, chars); // IntPtr -> char[] 的官方重载，无需 unsafe

        for (int i = 0; i + 4 <= buf.Length; i++)
        {
            if (buf[i] != '.' ||
                (buf[i + 1] | 0x20) != 'e' ||
                (buf[i + 2] | 0x20) != 'x' ||
                (buf[i + 3] | 0x20) != 'e')
                continue;

            int end = i + 4;
            int start = i;
            while (start > 0 && buf[start - 1] >= 32 && buf[start - 1] != '\0' && buf[start - 1] != (char)0xFFFF)
                start--;

            var path = new string(buf, start, end - start);
            int slash = path.LastIndexOfAny(new[] { '\\', '/' });
            var file = slash >= 0 ? path[(slash + 1)..] : path;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
            if (file.Length > 0) name = file;
            return path;
        }
        return null;
    }

    private static EventDetail BuildDetail(IntPtr eventRecord, ushort id, ushort task, byte opcode,
        int headerPid, int userDataLen, IntPtr userData)
    {
        var detail = new EventDetail
        {
            Id = id,
            Task = task,
            Opcode = opcode,
            Level = Marshal.ReadByte(eventRecord + 44),
            Channel = Marshal.ReadByte(eventRecord + 43),
            Version = Marshal.ReadByte(eventRecord + 42),
            Keyword = (ulong)Marshal.ReadInt64(eventRecord + 48),
            ProcessId = headerPid,
            ThreadId = Marshal.ReadInt32(eventRecord + 8),
            UserDataLength = userDataLen,
        };
        if (userData != IntPtr.Zero && userDataLen > 0 && userDataLen <= 8192)
        {
            var bytes = new byte[userDataLen];
            Marshal.Copy(userData, bytes, 0, userDataLen);
            detail.UserData = bytes;
        }
        return detail;
    }

    /// <summary>清掉上次异常退出残留的同名会话（否则 StartTrace 会返回 ERROR_ALREADY_EXISTS）。</summary>
    private void CleanUpExistingSession()
    {
        try
        {
            if (_properties == IntPtr.Zero) return;
            ControlTraceW(0, _sessionName, _properties, EVENT_TRACE_CONTROL_STOP);
        }
        catch { }
    }

    private void StopSession()
    {
        try
        {
            if (_properties != IntPtr.Zero)
                ControlTraceW(_sessionHandle, _sessionName, _properties, EVENT_TRACE_CONTROL_STOP);
        }
        catch { }
        try
        {
            if (_traceHandle != 0) CloseTrace(_traceHandle);
        }
        catch { }
        _traceHandle = 0;
        _sessionHandle = 0;
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try { _cts.Cancel(); } catch { }
        StopSession();
        try { _traceThread?.Join(1500); } catch { }
        _traceThread = null;
        if (_sessionNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_sessionNamePtr);
            _sessionNamePtr = IntPtr.Zero;
        }
        if (_properties != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = IntPtr.Zero;
        }
        DiagLog.Write("EtwProcessWatcher: 已停止");
    }
}

/// <summary>一条 ETW 事件的详细信息（自测排查用）。</summary>
public sealed class EventDetail
{
    public ushort Id { get; set; }
    public ushort Task { get; set; }
    public byte Opcode { get; set; }
    public byte Level { get; set; }
    public byte Channel { get; set; }
    public byte Version { get; set; }
    public ulong Keyword { get; set; }
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }
    public int UserDataLength { get; set; }
    public byte[]? UserData { get; set; }

    /// <summary>payload 前若干个 uint32（看字段排布用）。</summary>
    public string UInt32Dump(int count = 9)
    {
        if (UserData == null) return "<empty>";
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i + 3 < UserData.Length && parts.Count < count; i += 4)
            parts.Add(BitConverter.ToUInt32(UserData, i).ToString());
        return string.Join(", ", parts);
    }

    /// <summary>payload 里所有长度 ≥ 3 的 UTF-16 字符串（映像名 / 命令行在最前面几个）。</summary>
    public string Utf16Strings()
    {
        if (UserData == null) return "";
        var strings = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < UserData.Length; i += 2)
        {
            char c = (char)(UserData[i] | (UserData[i + 1] << 8));
            if (c >= 32 && c != 0xFFFF) sb.Append(c);
            else
            {
                if (sb.Length >= 3) strings.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length >= 3) strings.Add(sb.ToString());
        return string.Join(" || ", strings);
    }

    /// <summary>把 UserData 里可见的 ASCII / UTF-16 片段提出来（看 payload 结构用）。</summary>
    public string UserDataPreview()
    {
        if (UserData == null || UserData.Length == 0) return "<empty>";
        var text = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < UserData.Length; i += 2)
        {
            char c = (char)(UserData[i] | (UserData[i + 1] << 8));
            text.Append(char.IsControl(c) ? '.' : c);
        }
        return $"len={UserDataLength} hex={Convert.ToHexString(UserData, 0, Math.Min(UserData.Length, 48))} u32=[{UInt32Dump()}] str=[{Utf16Strings()}]";
    }
}
