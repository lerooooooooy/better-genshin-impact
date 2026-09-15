using System;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 模板匹配工作流节点:组合 <see cref="TemplateWaitTarget"/>,检测逻辑完全复用(懒加载 + score ≥ threshold 命中)。
/// 命中帧上的 ROI 画框 / 点击坐标由 <paramref name="operation"/> lambda 负责(可调 <see cref="ZzzTriggerActions"/>)。
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
        Action<ZzzCaptureContent, TemplateMatchResult?> operation,
        ZzzOverlayWindow? overlay = null)
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
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override IWorkflowNode? FailTemplate { get; internal set; }
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Dispose() => _target.Dispose();
}
