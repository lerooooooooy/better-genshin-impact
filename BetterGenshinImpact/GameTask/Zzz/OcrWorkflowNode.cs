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
        _target = new OcrWaitTarget(label, roi, targetText);
        Operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* OcrWaitTarget 不持资源 */ }
}
