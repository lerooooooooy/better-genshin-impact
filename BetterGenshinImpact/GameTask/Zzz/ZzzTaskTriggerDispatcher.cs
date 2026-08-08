using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Drawing;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Windows;
using Fischless.GameCapture;

using Timer = System.Timers.Timer;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 独立 timer、capture 生命周期、overlay 生命周期。与原神子系统完全解耦。
/// </summary>
public sealed class ZzzTaskTriggerDispatcher : IDisposable
{
    private readonly Timer _timer = new();
    private readonly Timer _overlayRefreshTimer = new();
    private readonly object _locker = new();

    private IGameCapture? _capture;
    private List<IZzzTaskTrigger> _triggers = new();
    private ZzzOverlayWindow? _overlay;
    private nint _hWnd;
    private int _frameIndex;

    public IGameCapture? GameCapture => _capture;

    public bool IsRunning => _capture != null;

    public event EventHandler? UiTaskStopTickEvent;
    public event EventHandler? UiTaskStartTickEvent;

    /// <summary>
    /// 日常任务序列执行完成或无法继续时触发,供 VM 关闭 UI 开关。
    /// </summary>
    public event EventHandler? DailyTaskFinishedEvent;

    public ZzzTaskTriggerDispatcher()
    {
        _timer.Elapsed += Tick;
        _timer.AutoReset = true;

        _overlayRefreshTimer.Interval = 100;
        _overlayRefreshTimer.AutoReset = true;
        _overlayRefreshTimer.Elapsed += OnOverlayRefresh;

        ProbeIntegrityLevel();
    }

    public void Start(nint hWnd, CaptureModes mode, bool runDailyTask = false, bool runEmptyTrigger = true, bool runTestTrigger = false)
    {
        Stop(); // 兜底

        _hWnd = hWnd;
        _frameIndex = 0;
        // 不再调 ActivateWindow:PostMessage 测试已验证 ZZZ 后台消息通路可用
        // (BetterGI High→ZZZ Medium 不被 UIPI 拦,配合同步阻塞 + ZZZ 自身 GetMessage 即可消费)。
        // 启动时切前台会抢 BetterGI 焦点,触发器命中后不再切前台(对齐 EmptyZzzTaskTrigger 设计)。

        _capture = GameCaptureFactory.Create(mode);
        // BitBltCapture.Start 收到 settings=null 会直接 return，必须显式传。
        try
        {
            _capture.Start(hWnd, new Dictionary<string, object>
            {
                { "autoFixWin11BitBlt", false },
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ] capture start error: {ex}");
            return;
        }

        // 提前到 trigger 创建之前：TestZzzTaskTrigger 需要拿到 overlay 引用，自己直接画框（不走回调）。
        ShowOverlay(hWnd);

        var triggers = new List<IZzzTaskTrigger>();
        if (runDailyTask)
        {
            triggers.Add(CreateDailyTrigger());
        }

        if (runEmptyTrigger)
        {
            triggers.Add(new EmptyZzzTaskTrigger(
                onMatch: (rect, content, label) => DrawOverlayRect(rect, content, label),
                onClick: (point, content) => DrawClickDot(point, content)));
        }

        if (runTestTrigger)
        {
            triggers.Add(CreateTestTrigger());
        }

        _triggers = triggers;

        _timer.Interval = 50;
        _timer.Start();
        _overlayRefreshTimer.Start();
        UiTaskStartTickEvent?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _timer.Stop();
        _overlayRefreshTimer.Stop();

        try
        {
            _capture?.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ] capture stop error: {ex}");
        }

        _capture?.Dispose();
        _capture = null;

        foreach (var t in _triggers)
        {
            (t as IDisposable)?.Dispose();
        }

        _triggers = new List<IZzzTaskTrigger>();
        _frameIndex = 0;
        _hWnd = 0;

        HideOverlay();
        UiTaskStopTickEvent?.Invoke(this, EventArgs.Empty);
    }

    private DailyTaskZzzTrigger CreateDailyTrigger()
    {
        return new DailyTaskZzzTrigger(
            onMatch: (rect, content, label) => DrawOverlayRect(rect, content, label),
            onClick: (point, content) => DrawClickDot(point, content),
            onFinished: () => DailyTaskFinishedEvent?.Invoke(this, EventArgs.Empty));
    }

    private TestZzzTaskTrigger CreateTestTrigger()
    {
        return new TestZzzTaskTrigger(
            captureProvider: BuildCaptureProvider(),
            overlay: _overlay,
            postMessageSimulator: TaskContext.Instance().PostMessageSimulator);
    }

    /// <summary>
    /// 构造一个 captureProvider 闭包:每次调用都新截一帧并包成 ZzzCaptureContent。
    /// 闭包捕获 this(dispatcher 整个生命周期内实例不变);Stop 时 _capture 置 null,provider 自然返回 null。
    /// 线程安全:Capture() 各自实现保证;_capture / _hWnd 字段读在 .NET 上对后台线程可见性可接受(Stop 时 timer 先停,无新 Tick 触发)。
    /// 整段 try/catch:兜住 native 调用(Capture / GetCaptureRect / GetScale)抛的异常,避免 background 任务挂掉或异常逃逸成 UnobservedTaskException。
    /// </summary>
    private Func<ZzzCaptureContent?> BuildCaptureProvider()
    {
        return () =>
        {
            try
            {
                var capture = _capture;
                if (capture == null || !capture.IsCapturing) return null;
                var frame = capture.Capture();
                if (frame?.Frame == null) return null;
                var captureRect = SystemControl.GetCaptureRect(_hWnd);
                var dpiScale = DpiHelper.GetScale(_hWnd).Y;
                return new ZzzCaptureContent(
                    frame.Frame, _frameIndex, _timer.Interval, _hWnd, captureRect, dpiScale);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Test] capture provider exception: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        };
    }

    /// <summary>
    /// 热挂载/卸载日常任务 trigger。仅在运行中生效(未运行时下次 Start 按 config 挂载)。
    /// 用户重新打开开关 → 重建全新实例从 seq 0 再跑;关闭 → 移除并释放。
    /// 在 _locker 内改动 _triggers,与 Tick 遍历互斥。
    /// </summary>
    public void SetDailyTaskEnabled(bool enabled)
    {
        lock (_locker)
        {
            if (_capture == null)
            {
                return;
            }

            var hasDaily = _triggers.Exists(t => t is DailyTaskZzzTrigger);
            if (enabled == hasDaily)
            {
                return;
            }

            var newList = new List<IZzzTaskTrigger>(_triggers);
            if (enabled)
            {
                newList.Add(CreateDailyTrigger());
                Debug.WriteLine("[ZZZ] daily trigger 已挂载(重新打开)");
            }
            else
            {
                var daily = newList.Find(t => t is DailyTaskZzzTrigger);
                if (daily != null)
                {
                    newList.Remove(daily);
                    (daily as IDisposable)?.Dispose();
                }

                Debug.WriteLine("[ZZZ] daily trigger 已卸载");
            }

            _triggers = newList;
        }
    }

    /// <summary>
    /// 热挂载/卸载测试 trigger。模式与 SetDailyTaskEnabled 一致,见该方法注释。
    /// </summary>
    public void SetTestTriggerEnabled(bool enabled)
    {
        lock (_locker)
        {
            if (_capture == null)
            {
                return;
            }

            var hasTest = _triggers.Exists(t => t is TestZzzTaskTrigger);
            if (enabled == hasTest)
            {
                return;
            }

            var newList = new List<IZzzTaskTrigger>(_triggers);
            if (enabled)
            {
                newList.Add(CreateTestTrigger());
                Debug.WriteLine("[ZZZ] test trigger 已挂载(重新打开)");
            }
            else
            {
                var test = newList.Find(t => t is TestZzzTaskTrigger);
                if (test != null)
                {
                    newList.Remove(test);
                    (test as IDisposable)?.Dispose();
                }

                Debug.WriteLine("[ZZZ] test trigger 已卸载");
            }

            _triggers = newList;
        }
    }

    private void Tick(object? sender, ElapsedEventArgs e)
    {
        // 抢锁失败直接丢弃(同一帧不重复算)。
        if (!Monitor.TryEnter(_locker, 0))
        {
            return;
        }

        try
        {
            GameCaptureFrame? frame;
            try
            {
                frame = _capture?.Capture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ] capture error: {ex}");
                return;
            }

            if (frame is null)
            {
                return; // 最小化/失焦等情况
            }

            var intervalMs = _timer.Interval;
            _frameIndex = (_frameIndex + 1) % Math.Max(1, (int)(60_000 / intervalMs));

            // 每帧重新计算 captureRect(物理像素) + dpiScale(用游戏 hWnd 所在监视器拿)。
            var captureRect = SystemControl.GetCaptureRect(_hWnd);
            var dpiScale = DpiHelper.GetScale(_hWnd).Y;

            using var content = new ZzzCaptureContent(frame.Frame, _frameIndex, intervalMs, _hWnd, captureRect, dpiScale);
            foreach (var trigger in _triggers)
            {
                if (!trigger.IsEnabled)
                {
                    continue;
                }

                try
                {
                    trigger.OnCapture(content);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ] trigger {trigger.Name} error: {ex}");
                }
            }
        }
        finally
        {
            Monitor.Exit(_locker);
        }
    }

    private void OnOverlayRefresh(object? sender, ElapsedEventArgs e)
    {
        var overlay = _overlay;
        if (overlay == null)
        {
            return;
        }

        // 必须切到 UI 线程执行，否则 WPF DP 抛"调用线程无法访问此对象"。
        Application.Current?.Dispatcher.BeginInvoke(() => overlay.RefreshPosition());
    }

    /// <summary>
    /// 只快照 RECT? 和 float 两个值，不传 Mat。静态化供 <see cref="TemplateOverlayRunner"/> 等调用方复用。
    /// </summary>
    public static void DrawMatchRect(ZzzOverlayWindow? overlay, DrawingRectangle rect, ZzzCaptureContent content, string label)
    {
        if (overlay == null)
        {
            return;
        }

        var capturedCaptureRect = content.CaptureRect;
        var capturedDpiScale = content.DpiScale;
        Application.Current?.Dispatcher.BeginInvoke(() =>
            overlay.SetMatchRect(rect, capturedCaptureRect, capturedDpiScale, label));
    }

    /// <summary>
    /// 实例包装：把当前 dispatcher 的 <see cref="_overlay"/> 作为参数传入 <see cref="DrawMatchRect"/>。
    /// 保留实例方法供 dispatcher 内部 lambda 直接调用（少打一个 overlay 参数）。
    /// </summary>
    private void DrawOverlayRect(DrawingRectangle rect, ZzzCaptureContent content, string label)
        => DrawMatchRect(_overlay, rect, content, label);

    private void DrawClickDot(DrawingPoint point, ZzzCaptureContent content)
    {
        var overlay = _overlay;
        if (overlay == null)
        {
            return;
        }

        var capturedCaptureRect = content.CaptureRect;
        var capturedDpiScale = content.DpiScale;
        Application.Current?.Dispatcher.BeginInvoke(() =>
            overlay.AddClickDot(point, capturedCaptureRect, capturedDpiScale));
    }

    private void ShowOverlay(nint hWnd)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _overlay = new ZzzOverlayWindow();
            _overlay.AttachTo(hWnd);
            _overlay.Show();
        });
    }

    private void HideOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        if (overlay == null)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() => overlay.Hide());
    }

    /// <summary>
    /// dispatcher 构造时读 OpenProcessToken + GetTokenInformation + ConvertSidToStringSidW，
    /// 打 [ZZZ] BetterGI integrity=S-1-16-12288。
    /// BetterGI 是 High、ZZZ 是 Medium 时仍走前台 SendInput:不是 UIPI 卡 PostMessage(UIPI 只拦低→高,
    /// High→Medium 不拦),而是 ZZZ 用 DX 全屏优化 + RawInput/DirectInput 管线,不读 WM_KEYDOWN,
    /// PostMessage 投递后消息进游戏 user32 队列但游戏从不 GetMessage 消费。SendInput 模拟硬件事件,
    /// 经 RIT(系统进程)直接转给前台线程,不依赖 user32 消息泵。
    /// </summary>
    private static void ProbeIntegrityLevel()
    {
        var hToken = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.Handle, TOKEN_QUERY, out hToken))
            {
                return;
            }

            GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size == 0)
            {
                return;
            }

            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(hToken, TokenIntegrityLevel, buffer, size, out _))
            {
                return;
            }

            var til = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
            if (ConvertSidToStringSidW(til.Label.Sid, out var sidPtr))
            {
                var sid = Marshal.PtrToStringUni(sidPtr);
                LocalFree(sidPtr);
                Debug.WriteLine($"[ZZZ] BetterGI integrity={sid}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ] integrity probe error: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (hToken != IntPtr.Zero)
            {
                CloseHandle(hToken);
            }
        }
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSidW(IntPtr Sid, out IntPtr StringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public void Dispose()
    {
        Stop();
        var overlay = _overlay;
        _overlay = null;
        Application.Current?.Dispatcher.Invoke(() => overlay?.Close());
        _timer.Dispose();
        _overlayRefreshTimer.Dispose();
    }
}
