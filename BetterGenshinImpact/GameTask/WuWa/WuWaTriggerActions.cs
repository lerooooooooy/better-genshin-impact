using System.Threading;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Trigger;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮 trigger 的动作原语。一律走 PostMessage 后台投递,不抢焦点、不切前台。
/// </summary>
public static class WuWaTriggerActions
{
    // 同一 hwnd 复用同一个 PostMessageSimulator,hwnd 变了(游戏重启/换实例)时重建。
    private static readonly object _bgLock = new();
    private static PostMessageSimulator? _bgSim;
    private static nint _bgSimHwnd = nint.Zero;

    private static PostMessageSimulator GetBgSim(nint hwnd)
    {
        lock (_bgLock)
        {
            if (_bgSim == null || _bgSimHwnd != hwnd)
            {
                _bgSim = Simulation.PostMessage(hwnd);
                _bgSimHwnd = hwnd;
            }

            return _bgSim;
        }
    }

    /// <summary>
    /// 后台点击 capture 坐标内的一点(不抢焦点)。
    /// PostMessage 的 lParam 用客户区坐标,而 capture 像素坐标本身就是相对客户区左上角的,直接传即可。
    /// </summary>
    public static void ClickCapturePointBackground(TriggerCaptureContent content, int captureX, int captureY)
    {
        GetBgSim(content.Hwnd).LeftButtonClickBackground(captureX, captureY);
    }

    /// <summary>
    /// 后台点击模板命中矩形的中心(不抢焦点)。
    /// </summary>
    public static void ClickMatchedAreaBackground(TemplateMatchResult result, TriggerCaptureContent content)
    {
        var abs = result.AbsRect;
        ClickCapturePointBackground(content, abs.X + abs.Width / 2, abs.Y + abs.Height / 2);
    }

    /// <summary>
    /// 后台点击 capture 坐标矩形内的中心(不抢焦点)。
    /// </summary>
    public static void ClickRectBackground(CvRect rect, TriggerCaptureContent content)
    {
        ClickCapturePointBackground(content, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>
    /// 后台投递按键(PostMessage WM_KEYDOWN / WM_KEYUP),不抢焦点。
    /// 按住 50ms 再抬起 —— 短于这个时长的按键可能被游戏当成原子 KeyPress 过滤掉。
    /// 鸣潮会吃后台投递的键盘消息(ESC 有效),但不吃后台投递的鼠标消息。
    /// </summary>
    public static void PressKeyBackground(TriggerCaptureContent content, User32.VK vk)
    {
        if (content.Hwnd == 0)
        {
            return;
        }

        var sim = GetBgSim(content.Hwnd);
        sim.KeyDownBackground(vk);
        Thread.Sleep(50);
        sim.KeyUpBackground(vk);
    }

    /// <summary>
    /// 点击 capture 坐标内的一点。返回 true = 走的前台 SendInput(真实鼠标事件),
    /// false = 走的后台 PostMessage(鸣潮在后台时 UE 不吃,基本无效)。
    ///
    /// 鸣潮是 UE 游戏,实测**不消费后台投递的鼠标消息**:PostMessage 的 WM_LBUTTONDOWN
    /// (带或不带 WM_MOUSEMOVE、伪造 WM_ACTIVATE 都试过)对「提示」弹窗的按钮完全无反应。
    /// 键盘方向相反 —— 后台投递的 ESC 鸣潮会吃。
    ///
    /// 所以:鸣潮窗口**已经是前台窗口**时走 SendInput 发真实鼠标事件(ZZZ 同样是这个结论),
    /// 此时不动焦点、不抢前台;鸣潮在后台时退回 PostMessage,不抢焦点(大概率不生效,但不会打扰用户)。
    /// 任何情况下都不调 ActivateWindow 把鸣潮拉到前台。
    /// </summary>
    public static bool ClickCapturePoint(TriggerCaptureContent content, int captureX, int captureY)
    {
        if (!IsForeground(content.Hwnd))
        {
            ClickCapturePointBackground(content, captureX, captureY);
            return false;
        }

        // capture 像素 == 客户区像素,SendInput 要的是屏幕坐标。
        var pt = new POINT(captureX, captureY);
        User32.ClientToScreen(content.Hwnd, ref pt);
        var screenWidth = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var screenHeight = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        if (screenWidth <= 1 || screenHeight <= 1)
        {
            return false;
        }

        var absX = pt.X * 65535.0 / (screenWidth - 1);
        var absY = pt.Y * 65535.0 / (screenHeight - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
        return true;
    }

    /// <summary>
    /// 目标窗口是不是当前前台窗口。
    /// 句柄要归一化到顶层窗口再比 —— 鸣潮的 hwnd 可能来自手动选窗,跟真正的前台顶层窗口对不上。
    /// </summary>
    public static bool IsForeground(nint hWnd)
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        return User32.GetAncestor(foreground, User32.GetAncestorFlag.GA_ROOT)
               == User32.GetAncestor(hWnd, User32.GetAncestorFlag.GA_ROOT);
    }
}
