using System;
using System.Diagnostics;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮「跳过」按钮(画面左上角的空心六边形)的工作流节点。
///
/// 检测不走模板 —— 按钮内部图标大面积透明,模板匹配得分上不去,
/// 用形态学顶帽 + 多边形几何(见 <see cref="WuWaImageUtils.DetectSkipButton"/>)。
///
/// 命中后**后台**投递一次 ESC(跳过键),按住 50ms 再抬起。
/// </summary>
public sealed class WuWaSkipButtonWorkflowNode : WorkflowNodeBase
{
    /// <summary>连发 ESC 的最小间隔(毫秒)。按钮在整个过场动画期间都存在,不限速的话每帧都会投递一次。</summary>
    private const int EscCooldownMs = 1000;

    private readonly ILogger _logger;

    /// <summary>上次投递 ESC 的时刻(Stopwatch 时间戳)。</summary>
    private long _lastEscTimestamp;

    /// <summary>本次连续命中是否已经打过日志。按钮在整个过场动画期间都在,逐帧打日志会刷屏。</summary>
    private bool _hitLogged;

    /// <summary>连续未识别到跳过按钮的帧数,用于日志。</summary>
    private int _missCount;

    /// <summary>最近一次命中的检测结果(画框用)。</summary>
    private WuWaSkipButton? _hit;

    public WuWaSkipButtonWorkflowNode(int maxRetries, ILogger logger, TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        _logger = logger;

        Label = "SkipButton";
        MaxRetries = maxRetries;
        SuccessTemplate = null;
        Operation = OnHit;
    }

    public override string Label { get; }

    public override int MaxRetries { get; internal set; }

    public override IWorkflowNode? SuccessTemplate { get; internal set; }

    public override IWorkflowNode? FailTemplate { get; internal set; }

    public override Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result)
    {
        // 检测结果不是模板匹配,基类画不了框 —— 画框放在 Operation 里自己做。
        result = null;

        _hit = WuWaImageUtils.DetectSkipButton(content.Image);
        if (_hit is null)
        {
            _missCount++;
            _hitLogged = false;
            if (_missCount % 100 == 1)
            {
                _logger.LogInformation("鸣潮跳过按钮:连续 {Count} 帧未识别到", _missCount);
            }

            return false;
        }

        _missCount = 0;
        return true;
    }

    private void OnHit(TriggerCaptureContent content, TemplateMatchResult? _)
    {
        var hit = _hit;
        if (hit is null)
        {
            return;
        }

        TriggerOverlayWindow.DrawMatchRect(
            Overlay,
            new System.Drawing.Rectangle(hit.Rect.X, hit.Rect.Y, hit.Rect.Width, hit.Rect.Height),
            content,
            $"SkipButton v{hit.Vertices}");

        if (!_hitLogged)
        {
            _hitLogged = true;
            _logger.LogInformation(
                "鸣潮跳过按钮命中: 中心=({X},{Y}) 尺寸={W}x{H} 顶点数={V} 填充度={Extent:F2} 实心度={Solidity:F2} 边缘梯度={Grad:F0} 中心暗亮比={Contrast:F2} 左右竖边={L}/{R}",
                hit.Center.X, hit.Center.Y, hit.Rect.Width, hit.Rect.Height, hit.Vertices, hit.Extent, hit.Solidity,
                hit.EdgeGradient, hit.CenterContrast, hit.FlatSideLeft, hit.FlatSideRight);
        }

        PressEscape(content);
    }

    /// <summary>
    /// 后台投递 ESC:PostMessage 到鸣潮窗口的 WM_KEYDOWN / WM_KEYUP,不抢焦点。
    /// </summary>
    private void PressEscape(TriggerCaptureContent content)
    {
        if (content.Hwnd == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if ((now - _lastEscTimestamp) * 1000.0 / Stopwatch.Frequency < EscCooldownMs)
        {
            return;
        }

        _lastEscTimestamp = now;

        WuWaTriggerActions.PressKeyBackground(content, User32.VK.VK_ESCAPE);
        Debug.WriteLine("[WuWa-SkipButton] ESC 已后台投递");
        _logger.LogInformation("鸣潮跳过按钮:已后台投递 ESC");
    }

    public override void Dispose()
    {
        // 检测不持资源(没有模板 Mat)。
    }
}
