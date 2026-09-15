using System;
using System.Diagnostics;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// "继续对话"箭头 »» 工作流节点:右下角白色 chevron 的 HSV 检测(7 道防线),命中后
/// 画 ROI 框 + 前台按空格。继承 <see cref="WorkflowNodeBase"/>,内部组合
/// <see cref="HsvWaitTarget"/> + 11 个检测常量 + 操作逻辑,触发器只需 new + 注册到 map。
///
/// 沿用 <see cref="TemplateWorkflowNode"/> / <see cref="OcrWorkflowNode"/> 的模式
/// (WorkflowNodeBase + 组合 WaitTarget),不直接继承 <see cref="HsvWorkflowNode"/>(后者 sealed)。
///
/// SuccessTemplate 默认 null —— 单次按空格后工作流终止;若需链到其他节点,触发器可构造后
/// 通过 map 写 <see cref="WorkflowNodeBase.SuccessTemplate"/>(同 assembly 内 internal set)。
///
/// 常量与 TestZzzTaskTrigger.RunSeqContinueArrow / DailyTask 一致便于阅读。
/// </summary>
public sealed class ContinueArrowWorkflowNode : WorkflowNodeBase
{
    // ROI:1920x1080 原生分辨率;手动截图(1922×1112 等)会偏移 ROI,见
    // project_zzz_continue_arrow_detection memory。
    private static readonly CvRect Roi = new(1462, 966, 42, 45);

    // HSV 颜色掩膜:箭头白灰 S≈0 远低于此,V≈150+ 高于此
    private const int SMax = 40;
    private const int VMin = 130;

    // 连通域形状过滤 7 道防线(由廉到贵):labels / area / height / aspect /
    // extent / largestFraction / convexity。详见 ZzzImageUtils.DetectArrowBlob。
    private const int MinArea = 80;
    private const int MaxArea = 400;
    private const int MinHeight = 16;
    private const double MinAspect = 1.3;
    private const double MinExtent = 0.4;
    private const double MinLargestFraction = 0.5;
    private const double MaxConvexity = 0.85;
    private const int MaxLabels = 2;

    private readonly HsvWaitTarget _target;

    public ContinueArrowWorkflowNode(int maxRetries, ZzzOverlayWindow? overlay = null)
        : base(overlay)
    {
        Label = "ContinueArrow";
        MaxRetries = maxRetries;
        SuccessTemplate = null;
        _target = new HsvWaitTarget("ContinueArrow",
            c => ZzzImageUtils.DetectArrowBlob(
                c.Image, Roi,
                SMax, VMin,
                MinArea, MaxArea,
                MinHeight,
                MinAspect, MinExtent,
                MinLargestFraction, MaxConvexity,
                MaxLabels));
        Operation = OnHit;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* HsvWaitTarget 不持资源 */ }

    private void OnHit(ZzzCaptureContent content, TemplateMatchResult? _)
    {
        // HSV 命中时 TemplateMatchResult 为 null,base class 不画 —— 手动画 ROI
        var safeRoi = ZzzImageUtils.ClampRoi(Roi, content.Image.Width, content.Image.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(
            Overlay,
            new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
            content,
            Label);
        Debug.WriteLine("[ZZZ-ContinueArrow] hit → press SPACE");
        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_SPACE, content);
    }
}
