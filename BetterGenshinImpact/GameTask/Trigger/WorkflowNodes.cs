using System;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Trigger;

/// <summary>
/// 模板匹配工作流节点:组合 <see cref="TemplateWaitTarget"/>,检测逻辑完全复用(懒加载 + score ≥ threshold 命中)。
/// 命中帧上的 ROI 画框 / 点击坐标由 <paramref name="operation"/> lambda 负责。
/// </summary>
public sealed class TemplateWorkflowNode : WorkflowNodeBase
{
    private readonly TemplateWaitTarget _target;

    public TemplateWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        string templatePath,
        double threshold,
        Action<TriggerCaptureContent, TemplateMatchResult?> operation,
        TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        if (string.IsNullOrEmpty(templatePath))
        {
            throw new ArgumentException("templatePath required", nameof(templatePath));
        }
        ArgumentNullException.ThrowIfNull(operation);

        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _target = new TemplateWaitTarget(label, templatePath, threshold);
        Operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; internal set; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override IWorkflowNode? FailTemplate { get; internal set; }
    public override Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() => _target.Dispose();
}

/// <summary>
/// OCR 文本工作流节点:组合 <see cref="OcrWaitTarget"/>,ROI 内 PaddleOCR 命中指定子串即算 hit。
/// 与 <see cref="HsvWorkflowNode"/> 同样命中时 TemplateMatchResult 为 null,operation 走 ROI 画框路径。
/// </summary>
public sealed class OcrWorkflowNode : WorkflowNodeBase
{
    private readonly OcrWaitTarget _target;

    /// <summary>
    /// 最近一次 TryMatch 命中时,PaddleOCR 返回的文字 region boundingRect(capture 坐标系);
    /// 透传自内部 <see cref="OcrWaitTarget.LastMatchBbox"/>。给 Operation lambda 用,
    /// 实现"点识别文字 bbox 内随机位置"而非 ROI 内随机位置。命中后 <see cref="WorkflowNodeBase.MatchAndOperation"/>
    /// 调用 Operation 之前会先调 TryMatch,此时 LastOcrBbox 已设置。
    /// miss 时为 null;Operation 应回退到 ROI。
    /// </summary>
    public CvRect? LastOcrBbox => _target.LastMatchBbox;

    /// <summary>当前节点的 OCR 搜索 ROI(供 Operation lambda 在 LastOcrBbox 为 null 时回退使用)。</summary>
    public CvRect Roi { get; }

    public OcrWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        CvRect roi,
        string targetText,
        Action<TriggerCaptureContent, TemplateMatchResult?> operation,
        TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        Roi = roi;
        _target = new OcrWaitTarget(label, roi, targetText);
        Operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; internal set; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override IWorkflowNode? FailTemplate { get; internal set; }
    public override Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* OcrWaitTarget 不持资源 */ }
}

/// <summary>
/// HSV / 任意 detector 工作流节点:组合 <see cref="HsvWaitTarget"/>,detector 抛异常被内部吞掉(避免打挂工作流)。
/// 命中时 TemplateMatchResult 为 null(operation 走 ROI 画框 / 固定点击区路径)。
/// </summary>
public sealed class HsvWorkflowNode : WorkflowNodeBase
{
    private readonly HsvWaitTarget _target;

    public HsvWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        Func<TriggerCaptureContent, bool> detector,
        Action<TriggerCaptureContent, TemplateMatchResult?> operation,
        TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(operation);

        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _target = new HsvWaitTarget(label, detector);
        Operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; internal set; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override IWorkflowNode? FailTemplate { get; internal set; }
    public override Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* HsvWaitTarget 不持资源 */ }
}
