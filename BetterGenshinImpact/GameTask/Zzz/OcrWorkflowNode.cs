using System;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

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
        Action<ZzzCaptureContent, TemplateMatchResult?> operation,
        ZzzOverlayWindow? overlay = null)
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
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override IWorkflowNode? FailTemplate { get; internal set; }
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* OcrWaitTarget 不持资源 */ }
}
