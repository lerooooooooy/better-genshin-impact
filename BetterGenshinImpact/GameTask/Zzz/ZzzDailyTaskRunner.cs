using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Genshin.Paths;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Extensions;
using BetterGenshinImpact.View.Windows;
using Fischless.GameCapture;
using Vanara.PInvoke;
using Wpf.Ui.Violeta.Controls;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 「执行日常任务」按钮的完整流程:检查 ZZZ 进程 → 找安装路径 → 启动 → 等「点击进入游戏」OCR → 随机点击进入。
/// 抽离自 ZzzStartPageViewModel.OnExecuteDailyTaskAsync,便于后续接入 daily task 触发链路时复用。
///
/// 失败/超时都通过 Toast / ThemedMessageBox 反馈,主流程不抛异常。capture 用完先 Stop 再 Dispose,不与 ZzzTaskTriggerDispatcher 抢 _capture。
/// </summary>
public sealed class ZzzDailyTaskRunner
{
    private readonly AllConfig _config;

    public ZzzDailyTaskRunner(AllConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 主入口。流程:
    /// <list type="number">
    ///   <item>ZZZ 已在运行 → Toast 提示并返回(暂不接 daily 序列)</item>
    ///   <item>ZZZ 未运行:解析路径(Config.ZzzInstallPath → RegistryGameLocator fallback)</item>
    ///   <item>路径空 → ThemedMessageBox 报错退出</item>
    ///   <item>调 <see cref="SystemControl.StartZzzFromLocalAsync"/> 启动 ZZZ;失败 → 报错退出</item>
    ///   <item>启动成功 → <see cref="WaitForEnterGameAsync"/> 30s OCR 轮询 + ROI 内随机点击</item>
    ///   <item>OCR 超时 → ThemedMessageBox 报错</item>
    ///   <item>命中 → Toast 成功(暂不接 daily 序列)</item>
    /// </list>
    /// </summary>
    /// <param name="selectedCaptureMode">ViewModel 透传的 dropdown 当前值(瞬时态);为空时用 Config.ZzzCaptureMode,再不行回落 BitBlt。</param>
    public async Task RunAsync(string? selectedCaptureMode = null)
    {
        var hWnd = SystemControl.FindZzzHandle();
        if (hWnd != IntPtr.Zero)
        {
            Toast.Information("绝区零已在运行,日常任务序列尚未实现");
            return;
        }

        var path = ResolveInstallPath();
        if (string.IsNullOrEmpty(path))
        {
            await ThemedMessageBox.ErrorAsync("未找到绝区零安装路径,无法自动启动。请手动启动游戏后重试,或在配置中填写 ZenlessZoneZero.exe 路径。");
            return;
        }

        hWnd = await SystemControl.StartZzzFromLocalAsync(path);
        if (hWnd == IntPtr.Zero)
        {
            await ThemedMessageBox.ErrorAsync("绝区零启动失败,请手动启动游戏后重试。");
            return;
        }

        await WaitForEnterGameAsync(hWnd, ResolveCaptureMode(selectedCaptureMode));

        // TODO: 后续对接 ZzzTaskTriggerDispatcher.Start + daily 序列
        Toast.Success("绝区零已启动,日常任务序列尚未实现");
    }

    /// <summary>
    /// 优先 Config.ZzzInstallPath,空则调 RegistryGameLocator 自动发现。
    /// 命中后写回 Config(由 OnAnyChangedAction 自动持久化)。
    /// </summary>
    private string? ResolveInstallPath()
    {
        if (!string.IsNullOrEmpty(_config.ZzzInstallPath))
        {
            return _config.ZzzInstallPath;
        }

        var detected = RegistryGameLocator.GetDefaultZzzInstallPath();
        if (!string.IsNullOrEmpty(detected))
        {
            _config.ZzzInstallPath = detected;
            return detected;
        }

        return null;
    }

    /// <summary>
    /// 截图模式解析:Config 优先,dropdown 瞬时值兜底,再不行 BitBlt。
    /// 与原 ZzzStartPageViewModel.GetCaptureMode 等价。
    /// </summary>
    private CaptureModes ResolveCaptureMode(string? selectedMode)
    {
        try
        {
            return string.IsNullOrEmpty(_config.ZzzCaptureMode)
                ? (selectedMode ?? CaptureModes.BitBlt.ToString()).ToCaptureMode()
                : _config.ZzzCaptureMode.ToCaptureMode();
        }
        catch
        {
            return CaptureModes.BitBlt;
        }
    }

    /// <summary>
    /// 临时起一个 capture,在 ROI (890, 869, 140x39) 内 OCR 轮询「点击进入游戏」文本,
    /// 命中后切前台 + 随机点击 ROI 内一点。
    /// 不与 dispatcher 抢 _capture / _locker,完成后立即 Stop + Dispose。
    /// 超时 30s / 间隔 1s。
    /// </summary>
    private async Task WaitForEnterGameAsync(nint hWnd, CaptureModes captureMode)
    {
        var capture = GameCaptureFactory.Create(captureMode);
        try
        {
            capture.Start(hWnd, new Dictionary<string, object>
            {
                { "autoFixWin11BitBlt", false },
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-EnterGame] capture.Start 失败: {ex.GetType().Name}: {ex.Message}");
            ThemedMessageBox.Error($"启动截图失败: {ex.Message}");
            capture.Dispose();
            return;
        }

        int frameIndex = 0;
        var captureProvider = new Func<ZzzCaptureContent?>(() =>
        {
            try
            {
                if (!capture.IsCapturing) return null;
                var frame = capture.Capture();
                if (frame?.Frame == null) return null;
                var captureRect = SystemControl.GetCaptureRect(hWnd);
                var dpiScale = DpiHelper.GetScale(hWnd).Y;
                frameIndex++;
                return new ZzzCaptureContent(
                    frame.Frame, frameIndex, 1000, hWnd, captureRect, dpiScale);
            }
            catch
            {
                return null;
            }
        });

        var enterGameRoi = new CvRect(890, 869, 140, 39);
        var ocrTarget = new OcrWaitTarget("EnterGame", enterGameRoi, "点击进入游戏");

        var (_, _, content) = ZzzWaitForHit.Run(
            totalTimeoutMs: 30000,
            intervalMs: 1000,
            captureProvider: captureProvider,
            ocrTarget);

        // 不管命中与否,先把 capture 关掉
        try { capture.Stop(); } catch (Exception ex) { Debug.WriteLine($"[ZZZ-EnterGame] capture.Stop 异常: {ex.Message}"); }
        capture.Dispose();

        if (content == null)
        {
            Debug.WriteLine("[ZZZ-EnterGame] OCR 等待「点击进入游戏」超时 30s");
            await ThemedMessageBox.ErrorAsync("未检测到「点击进入游戏」按钮,已超时 30 秒。请检查游戏是否已启动到登录界面。");
            return;
        }

        // 命中:在 ROI 内随机点一下(参考 testtrigger 的 ClickRect 模式)
        var safeRoi = ZzzImageUtils.ClampRoi(enterGameRoi, content.Image.Width, content.Image.Height);
        content.Dispose();

        SystemControl.ActivateWindow(hWnd);
        await Task.Delay(300);
        var randX = safeRoi.X + Random.Shared.Next(0, Math.Max(1, safeRoi.Width));
        var randY = safeRoi.Y + Random.Shared.Next(0, Math.Max(1, safeRoi.Height));
        ClickAtScreen(hWnd, randX, randY);

        Toast.Success("已点击「进入游戏」");
    }

    /// <summary>
    /// 与 TestZzzTaskTrigger.ClickAt 同公式(captureX/Y + captureRect → SendInput 屏幕坐标)。
    /// </summary>
    private static void ClickAtScreen(nint hWnd, int captureX, int captureY)
    {
        var captureRect = SystemControl.GetCaptureRect(hWnd);

        var screenX = captureX + captureRect.Left;
        var screenY = captureY + captureRect.Top;
        var sw = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var sh = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        if (sw <= 1 || sh <= 1)
        {
            Debug.WriteLine("[ZZZ-EnterGame] ClickAtScreen: invalid screen metrics");
            return;
        }

        var absX = screenX * 65535.0 / (sw - 1);
        var absY = screenY * 65535.0 / (sh - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
    }
}
