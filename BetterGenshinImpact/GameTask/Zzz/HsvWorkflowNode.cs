using System;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.GameTask.Zzz;

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
        Func<ZzzCaptureContent, bool> detector,
        Action<ZzzCaptureContent, TemplateMatchResult?> operation,
        ZzzOverlayWindow? overlay = null)
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
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() { /* HsvWaitTarget 不持资源 */ }
}
