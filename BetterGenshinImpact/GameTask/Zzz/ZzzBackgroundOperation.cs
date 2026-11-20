using BetterGenshinImpact.GameTask.Trigger;
using System;
using BetterGenshinImpact.GameTask;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 后台模式操作的 flag 状态 + 分支方法。镜像 Genshin AutoSkipTrigger 的
/// UseBackgroundOperation 模式:
///
/// <list type="bullet">
///   <item><see cref="IsBackgroundRunning"/>: 用户开关,由 NewZzzTaskTrigger ctor 从
///         Config.ZzzRunBackgroundEnabled 注入。</item>
///   <item><see cref="UseBackgroundOperation"/>: 运行期有效标志 = IsBackgroundRunning
///         AND ZZZ 不在前台(<see cref="SystemControl.IsZzzActive"/> 返回 false)。</item>
///   <item><see cref="RefreshOperationMode"/>: 用前台窗口状态刷新 useBackground,
///         每帧由 NewZzzTaskTrigger.OnCapture 开头调一次。</item>
///   <item>分支方法(<see cref="PressKey"/> / <see cref="ClickRect"/> / <see cref="ClickAt"/>
///         / <see cref="ClickMatchedArea"/>):内部按 useBackground 选 Foreground/Background 实现。</item>
/// </list>
///
/// Background 路径依赖 PostMessageSimulator(<see cref="TaskContext.Instance()"/>),
/// 走 user32 消息投递;不抢焦点。ZZZ 实际是否响应待用户实测(dispatcher 已有警告
/// ZZZ DX 全屏优化 + RawInput 可能不读 WM_KEYDOWN)。
///
/// 随机抖动保留:时间 300-400ms(避免重复间隔被检测) + 鼠标点击位置随机
/// (避免重复同一坐标被检测)+ 键盘 50ms 持续按压(<see feedback_zzz_input.md>)。
/// </summary>
public static class ZzzBackgroundOperation
{
    /// <summary>用户开关(从 config 注入)</summary>
    public static bool IsBackgroundRunning { get; set; }

    /// <summary>运行期有效标志:IsBackgroundRunning AND ZZZ 不在前台</summary>
    public static bool UseBackgroundOperation { get; private set; }

    /// <summary>
    /// 用前台窗口状态刷新 <see cref="UseBackgroundOperation"/>。每帧由 trigger 开头调一次。
    /// </summary>
    public static void RefreshOperationMode(nint zzzHwnd)
    {
        UseBackgroundOperation = IsBackgroundRunning && !SystemControl.IsZzzActive(zzzHwnd);
    }

    /// <summary>键盘:按 <see cref="UseBackgroundOperation"/> 选 Foreground / Background。</summary>
    public static void PressKey(User32.VK keyCode, TriggerCaptureContent content)
    {
        if (UseBackgroundOperation)
        {
            ZzzTriggerActions.PressKeyBackground(keyCode, content);
        }
        else
        {
            ZzzTriggerActions.PressKeyForeground(keyCode, content);
        }
    }

    /// <summary>鼠标:点给定 capture 矩形 (随机一点)。按 flag 选 Foreground / Background。</summary>
    public static void ClickRect(TriggerCaptureContent content, CvRect rect)
    {
        if (UseBackgroundOperation)
        {
            ZzzTriggerActions.ClickRectBackground(content, rect);
        }
        else
        {
            ZzzTriggerActions.ClickRect(content, rect);
        }
    }

    /// <summary>鼠标:点给定 capture 像素坐标。按 flag 选。</summary>
    public static void ClickAt(TriggerCaptureContent content, int x, int y)
    {
        if (UseBackgroundOperation)
        {
            ZzzTriggerActions.ClickAtBackground(content, x, y);
        }
        else
        {
            ZzzTriggerActions.ClickAt(content, x, y);
        }
    }

    /// <summary>鼠标:点模板匹配框内随机一点。按 flag 选 Foreground / Background。</summary>
    public static void ClickMatchedArea(TemplateMatchResult result, TriggerCaptureContent content)
    {
        if (UseBackgroundOperation)
        {
            ZzzTriggerActions.ClickMatchedAreaBackground(result, content);
        }
        else
        {
            ZzzTriggerActions.ClickMatchedArea(result, content);
        }
    }
}