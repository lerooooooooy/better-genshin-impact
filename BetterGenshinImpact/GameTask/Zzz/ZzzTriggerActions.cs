using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// ZZZ 子系统共享的 trigger 动作原语。被 TestZzzTaskTrigger / CommonZzzTaskTrigger 共用。
///
/// 集中"前台切焦点 + 随机抖动 + SendInput 按键/鼠标"流程,避免两个 trigger 重复实现。
/// 所有方法都是 public static,不依赖任何实例字段。
/// 抖动用 <see cref="Random.Shared"/>(.NET 6+ 线程安全),保证跨 trigger 并发调用也安全。
/// </summary>
public static class ZzzTriggerActions
{
    /// <summary>模拟人类反应时间,所有动作前随机等待 300-400ms</summary>
    public const int ActionDelayMinMs = 300;

    /// <summary>Random.Next 上界 exclusive</summary>
    public const int ActionDelayMaxMsExclusive = 401;

    private static readonly Random _rng = Random.Shared;

    /// <summary>
    /// 键盘动作:SendInput 按下指定虚拟键(KeyDown + 持续 50ms + KeyUp)模拟硬件事件走 RIT 投到前台线程。
    /// 对比后台 PostMessage:抢焦点但能跨 ZZZ DX 全屏优化触发 RawInput。
    ///
    /// 焦点切换只在前台被抢占或窗口被最小化时执行 — 避免反复调 ShowWindow(SW_RESTORE)
    /// 触发 ZZZ DX 全屏 swap chain 重初始化(实测会让 ZZZ + BetterGI 互相阻塞)。
    /// </summary>
    public static void PressKeyForeground(User32.VK keyCode, ZzzCaptureContent hitContent)
    {
        Thread.Sleep(1500);
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        Simulation.SendInput.Keyboard.KeyDown(keyCode);
        Thread.Sleep(50);
        Simulation.SendInput.Keyboard.KeyUp(keyCode);
    }

    /// <summary>
    /// 鼠标动作:SendInput 点击命中矩形内随机一点(不是固定中心,避免重复同一坐标被检测)。
    /// 屏幕坐标 = capture 坐标 + captureRect.Left/Top,然后用 65535 归一化喂给 SendInput。
    /// </summary>
    public static void ClickMatchedArea(TemplateMatchResult r, ZzzCaptureContent hitContent)
    {
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
        Thread.Sleep(1500);
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        var abs = r.AbsRect;
        var randX = abs.X + _rng.Next(0, Math.Max(1, abs.Width));
        var randY = abs.Y + _rng.Next(0, Math.Max(1, abs.Height));
        ClickAt(hitContent, randX, randY);
    }

    /// <summary>
    /// 鼠标动作:SendInput 点击给定 capture 坐标矩形内随机一点。
    /// 与 <see cref="ClickMatchedArea"/> 的区别:点击位置来自调用方指定的固定 UI 区域,
    /// 而非模板匹配的 absRect(用于匹配框 ≠ 实际按钮位置的场景,如 getBattery 弹窗)。
    /// </summary>
    public static void ClickRect(ZzzCaptureContent hitContent, CvRect rect)
    {
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
        Thread.Sleep(1500);
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        var randX = rect.X + _rng.Next(0, Math.Max(1, rect.Width));
        var randY = rect.Y + _rng.Next(0, Math.Max(1, rect.Height));
        ClickAt(hitContent, randX, randY);
    }

    /// <summary>
    /// 鼠标动作:SendInput 点击给定 capture 像素坐标。captureX/Y 加上 captureRect 后转屏幕坐标。
    /// </summary>
    public static void ClickAt(ZzzCaptureContent hitContent, int captureX, int captureY)
    {
        var captureRect = hitContent.CaptureRect;
        if (captureRect == null)
        {
            Debug.WriteLine("[ZZZ] ClickAt: captureRect is null");
            return;
        }

        var screenX = captureX + captureRect.Value.Left;
        var screenY = captureY + captureRect.Value.Top;
        var screenWidth = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var screenHeight = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        if (screenWidth <= 1 || screenHeight <= 1)
        {
            Debug.WriteLine("[ZZZ] ClickAt: invalid screen metrics");
            return;
        }

        var absX = screenX * 65535.0 / (screenWidth - 1);
        var absY = screenY * 65535.0 / (screenHeight - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
    }

    /// <summary>
    /// 在命中帧上画命中框。仅画框,不负责 Dispose content。
    /// 委托给 dispatcher 的静态 DrawMatchRect,跨线程安全由 dispatcher 兜底。
    /// 调用方传入自己的 overlay 引用;null 时跳过画框但仍跑匹配。
    /// </summary>
    public static void DrawHitRect(ZzzOverlayWindow? overlay, TemplateMatchResult result, ZzzCaptureContent content, string label)
    {
        var abs = result.AbsRect;
        var rect = new System.Drawing.Rectangle(abs.X, abs.Y, abs.Width, abs.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(overlay, rect, content, $"{label} hit {result.Score:F3}");
    }
}
