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

    public void Start(nint hWnd, CaptureModes mode, bool runDailyTask = false, bool runEmptyTrigger = true)
    {
        Stop(); // 兜底

        _hWnd = hWnd;
        _frameIndex = 0;

        // 激活窗口，保证后续 SendInput 命中 ZZZ(对齐原神 TaskTriggerDispatcher.Start)。
        // 这里只调一次；触发器命中后不再切换前台，避免抢焦点打断用户对 BetterGI 的操作。
        SystemControl.ActivateWindow(hWnd);

        _capture = GameCaptureFactory.Create(mode);
        // BitBltCapture.Start 收到 settings=null 会直接 return，必须显式传。
        _capture.Start(hWnd, new Dictionary<string, object>
        {
            { "autoFixWin11BitBlt", false },
        });

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

        _triggers = triggers;

        ShowOverlay(hWnd);

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
            Debug.WriteLine($"[ZZZ] capture stop error: {ex.Message}");
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
                Debug.WriteLine($"[ZZZ] capture error: {ex.Message}");
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

            Debug.WriteLine(
                $"[ZZZ] tick frame={_frameIndex} size={frame.Frame.Width}x{frame.Frame.Height} ts={DateTime.Now:HH:mm:ss.fff}");

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
                    Debug.WriteLine($"[ZZZ] trigger {trigger.Name} error: {ex.Message}");
                }
            }

            // 本帧末尾清掉上一帧残留的绿框/标签(不动红点)。
            var overlay = _overlay;
            if (overlay != null)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => overlay.ClearMatchRect());
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
    /// 只快照 RECT? 和 float 两个值，不传 Mat。
    /// </summary>
    private void DrawOverlayRect(DrawingRectangle rect, ZzzCaptureContent content, string label)
    {
        var overlay = _overlay;
        if (overlay == null)
        {
            return;
        }

        var capturedCaptureRect = content.CaptureRect;
        var capturedDpiScale = content.DpiScale;
        Application.Current?.Dispatcher.BeginInvoke(() =>
            overlay.SetMatchRect(rect, capturedCaptureRect, capturedDpiScale, label));
    }

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
    /// BetterGI 是 High、ZZZ 是 Medium 时 PostMessage 带坐标会被 UIPI 吞，所以走前台 SendInput。
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
