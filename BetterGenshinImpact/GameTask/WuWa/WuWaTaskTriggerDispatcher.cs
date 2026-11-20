using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Timers;
using System.Windows;
using Fischless.GameCapture;

using Timer = System.Timers.Timer;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮触发器调度器:独立 timer、capture 生命周期、overlay 生命周期。
/// 与原神 / 绝区零子系统完全解耦。
/// </summary>
public sealed class WuWaTaskTriggerDispatcher : IDisposable
{
    private static readonly ILogger Logger = App.GetLogger<WuWaTaskTriggerDispatcher>();

    private readonly Timer _timer = new();
    private readonly Timer _overlayRefreshTimer = new();
    private readonly object _locker = new();
    private readonly IMaskWindowHost _maskWindowHost;

    private IGameCapture? _capture;
    private List<IWuWaTaskTrigger> _triggers = new();
    private TriggerOverlayWindow? _overlay;
    private nint _hWnd;
    private int _frameIndex;

    private int _nullFrameCount;
    private int _dumpCounter;
    private int _dumpSeq;

    public IGameCapture? GameCapture => _capture;

    public bool IsRunning => _capture != null;

    public event EventHandler? UiTaskStopTickEvent;
    public event EventHandler? UiTaskStartTickEvent;

    public WuWaTaskTriggerDispatcher(IMaskWindowHost maskWindowHost)
    {
        _maskWindowHost = maskWindowHost;
        _timer.Elapsed += Tick;
        _timer.AutoReset = true;

        _overlayRefreshTimer.Interval = 100;
        _overlayRefreshTimer.AutoReset = true;
        _overlayRefreshTimer.Elapsed += OnOverlayRefresh;
    }

    public void Start(nint hWnd, CaptureModes mode, bool runCommonTrigger = false)
    {
        Stop(); // 兜底

        _hWnd = hWnd;
        _frameIndex = 0;

        _capture = GameCaptureFactory.Create(mode);
        try
        {
            _capture.Start(hWnd, new Dictionary<string, object>
            {
                { "autoFixWin11BitBlt", false },
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "鸣潮截图器启动失败");
            return;
        }

        // 提前到 trigger 创建之前:trigger 需要 overlay 引用来画命中框。
        ShowOverlay(hWnd);

        // 遮罩窗口跟随鸣潮窗口,游戏窗口左下角的日志框由它渲染。
        _maskWindowHost.Attach(hWnd);

        var triggers = new List<IWuWaTaskTrigger>();
        if (runCommonTrigger)
        {
            triggers.Add(CreateCommonTrigger());
        }

        _triggers = triggers;
        _nullFrameCount = 0;
        _dumpCounter = 0;

        Logger.LogInformation("鸣潮截图器已启动: hwnd=0x{Hwnd:X}, 模式={Mode}, 触发器数={Count}",
            hWnd, mode, triggers.Count);

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
            Logger.LogError(ex, "鸣潮截图器停止失败");
        }

        _capture?.Dispose();
        _capture = null;

        foreach (var t in _triggers)
        {
            (t as IDisposable)?.Dispose();
        }

        _triggers = new List<IWuWaTaskTrigger>();
        _frameIndex = 0;
        _hWnd = 0;

        _maskWindowHost.Detach();
        HideOverlay();
        UiTaskStopTickEvent?.Invoke(this, EventArgs.Empty);
    }

    private CommonWuWaTaskTrigger CreateCommonTrigger()
    {
        return new CommonWuWaTaskTrigger(_overlay);
    }

    /// <summary>
    /// 热挂载/卸载通用 trigger。
    /// </summary>
    public void SetCommonTriggerEnabled(bool enabled)
    {
        lock (_locker)
        {
            if (_capture == null)
            {
                return;
            }

            var has = _triggers.Exists(t => t is CommonWuWaTaskTrigger);
            if (enabled == has)
            {
                return;
            }

            var newList = new List<IWuWaTaskTrigger>(_triggers);
            if (enabled)
            {
                newList.Add(CreateCommonTrigger());
                Debug.WriteLine("[WuWa] common trigger 已挂载");
            }
            else
            {
                var trig = newList.Find(t => t is CommonWuWaTaskTrigger);
                if (trig != null)
                {
                    newList.Remove(trig);
                    (trig as IDisposable)?.Dispose();
                }

                Debug.WriteLine("[WuWa] common trigger 已卸载");
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
                Logger.LogError(ex, "鸣潮截图失败");
                return;
            }

            if (frame is null)
            {
                _nullFrameCount++;
                // 连续空帧说明截图器抓不到画面(游戏最小化/被遮挡/模式不兼容),要让人知道。
                if (_nullFrameCount % 100 == 1)
                {
                    Logger.LogWarning("鸣潮截图:连续 {Count} 帧空画面", _nullFrameCount);
                }

                // 空帧一般是游戏最小化/被遮挡,遮罩保持原状不乱跳。
                ReportGameWindow(capturing: true);
                return; // 最小化/失焦等情况
            }

            _nullFrameCount = 0;

            var intervalMs = _timer.Interval;
            _frameIndex = (_frameIndex + 1) % Math.Max(1, (int)(60_000 / intervalMs));

            // 每帧重新计算 captureRect(物理像素) + dpiScale(用游戏 hWnd 所在监视器拿)。
            var captureRect = SystemControl.GetCaptureRect(_hWnd);
            var dpiScale = DpiHelper.GetScale(_hWnd).Y;

            ReportGameWindow(capturing: true);

            using var content = new TriggerCaptureContent(frame.Frame, _frameIndex, intervalMs, _hWnd, captureRect, dpiScale);
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
                    Logger.LogError(ex, "鸣潮触发器 {Name} 异常", trigger.Name);
                }
            }

            DumpSkipButtonRoi(frame);
        }
        finally
        {
            Monitor.Exit(_locker);
        }
    }

    /// <summary>
    /// 上报游戏窗口状态。遮罩窗口(游戏窗口左下角的日志框)据此决定显隐和跟随位置,
    /// 显隐策略统一由遮罩宿主判断,这里只报事实。
    /// </summary>
    private void ReportGameWindow(bool capturing)
    {
        _maskWindowHost.ReportGameWindow(new GameWindowState(
            capturing,
            SystemControl.IsWuWaActiveByProcess(_hWnd),
            SystemControl.IsWuWaMinimized(_hWnd),
            SystemControl.IsForegroundOwnedByBetterGiOrGame(_hWnd),
            SystemControl.GetCaptureRect(_hWnd)));
    }

    /// <summary>
    /// 诊断:把「跳过」按钮搜索区域连续落盘,便于离线比对识别结果。
    /// 只写 ROI(几十 KB),滚动保留最近若干张,避免整帧落盘把磁盘写满。
    /// </summary>
    private void DumpSkipButtonRoi(GameCaptureFrame frame)
    {
        // 每 5 帧(50ms 一帧 → 250ms)落盘一次。
        if (++_dumpCounter < 5)
        {
            return;
        }

        _dumpCounter = 0;
        try
        {
            var dumpDir = Path.Combine(AppContext.BaseDirectory, "log", "wuwa_dump");
            Directory.CreateDirectory(dumpDir);
            using var roi = new Mat(frame.Frame, WuWaImageUtils.GetSkipButtonRoi(frame.Frame.Height));
            Cv2.ImWrite(Path.Combine(dumpDir, $"f{_dumpSeq++:D6}.png"), roi);

            // 滚动清理,只保留最近 400 张(约 100 秒)。
            var files = Directory.GetFiles(dumpDir, "*.png");
            if (files.Length > 400)
            {
                foreach (var old in files.OrderBy(f => f).Take(files.Length - 400))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "鸣潮 ROI 落盘失败");
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

    private void ShowOverlay(nint hWnd)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _overlay = new TriggerOverlayWindow
            {
                // overlay 只用来画框,不能激活:激活它会把用户的焦点抢走。
                ShowActivated = false,
            };
            _overlay.AttachTo(hWnd);
            _overlay.Show();
            Logger.LogInformation("鸣潮 overlay 已创建: 可见={Visible} 位置=({Left},{Top}) {W}x{H}",
                _overlay.IsVisible, _overlay.Left, _overlay.Top, _overlay.Width, _overlay.Height);
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
