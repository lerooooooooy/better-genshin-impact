using System;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// <see cref="IWorkflowNode"/> 的抽象基类:提供 <see cref="MatchAndOperation"/> 默认实现 + retry counter。
///
/// 为什么不放默认接口方法:C# 8+ 默认接口方法拿不到每个实例的 backing state,
/// retry counter 是实例字段,放接口里会让每个具体类重复写。
///
/// retry 语义(跨调用累计,实例持久):
/// - miss → RetryCount++,RetryCount &lt; MaxRetries 时返回 this,达到 MaxRetries 返回 null(工作流结束)
/// - hit → RetryCount = 0,Operation 执行完后返回 SuccessTemplate(null = 工作流结束)
/// - Operation 抛异常 → 自然上浮给 caller,counter 不变
/// </summary>
public abstract class WorkflowNodeBase : IWorkflowNode
{
    public abstract string Label { get; }
    public abstract int MaxRetries { get; }
    public int RetryCount { get; private set; }
    public abstract IWorkflowNode? SuccessTemplate { get; internal set; }

    public abstract bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result);
    public abstract Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }
    public abstract void Dispose();

    /// <summary>
    /// 画框用的 overlay(由 trigger 在构造节点时传入)。非 null 时,base class 在命中后、
    /// Operation 前自动调 <see cref="ZzzTriggerActions.DrawHitRect"/> 在 overlay 上画命中框。
    /// null 时跳过画框 —— 测试 mock 或不需要调试可见性的场景使用。
    /// </summary>
    public ZzzOverlayWindow? Overlay { get; }

    protected WorkflowNodeBase(ZzzOverlayWindow? overlay = null)
    {
        Overlay = overlay;
    }

    public IWorkflowNode? MatchAndOperation(ZzzCaptureContent content, out TemplateMatchResult? result)
    {
        bool hit = TryMatch(content, out result);
        if (!hit)
        {
            RetryCount++;
            return RetryCount >= MaxRetries ? null : this;
        }
        RetryCount = 0;
        // 命中时统一画框(overlay 非 null 且 result 非 null 才画;label 用节点自己的 Label)
        if (Overlay != null && result != null)
        {
            ZzzTriggerActions.DrawHitRect(Overlay, result, content, Label);
        }
        Operation(content, result);
        return SuccessTemplate;
    }
}
