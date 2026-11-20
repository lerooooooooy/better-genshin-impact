using System;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.GameTask.Trigger;

/// <summary>
/// 工作流节点:检测 + 重试 + 命中动作 + 下一节点四件套。
/// 由 trigger 在每帧驱动,任一帧命中即执行 Operation 并切换到 SuccessTemplate;
/// 连续 miss 到 MaxRetries 返回 FailTemplate(null = 终止工作流)。
///
/// 与 <see cref="IWaitTarget"/> 并存(后者用于单帧多目标扫描的 delegate-onHit 模式);
/// 本接口是"命中后按链推进"的工作流形态。
/// </summary>
public interface IWorkflowNode : IDisposable
{
    /// <summary>节点名(日志 / 调试用)。</summary>
    string Label { get; }

    /// <summary>连续 miss 超过此值返回 null,终止工作流。</summary>
    int MaxRetries { get; }

    /// <summary>当前失败计数(只读)。命中后归零。</summary>
    int RetryCount { get; }

    /// <summary>命中后切换到的下一节点;null = 工作流结束。</summary>
    IWorkflowNode? SuccessTemplate { get; }

    /// <summary>
    /// 重试耗尽后切换到的失败路径节点(替代原本直接返回 null 终止);
    /// null = 失败时直接终止工作流(老行为)。设置后,retry 耗尽返回此节点,允许
    /// "主路径失败 → 走备选 OCR / 不同检测"这类分支恢复逻辑。
    /// </summary>
    IWorkflowNode? FailTemplate { get; }

    /// <summary>
    /// 在给定 capture 帧上尝试匹配。
    /// 命中返回 true,模板命中时 <paramref name="result"/> 非 null。
    /// 内部实现应捕获异常并视为 miss(避免单次抛错打挂整个工作流)。
    /// </summary>
    bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result);

    /// <summary>
    /// 命中后执行的动作(由构造函数或后续 setter 注入的 lambda)。异常自然上浮给 caller。
    /// 属性类型是 <see cref="Action{T1, T2}"/>,可直接像方法一样调用 <c>node.Operation(c, r)</c>。
    /// </summary>
    Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; }

    /// <summary>
    /// 工作流节点的核心入口:单帧 tryMatch + 命中则 Operation + 返回下一节点。
    /// 默认实现在 <see cref="WorkflowNodeBase"/>。
    /// </summary>
    IWorkflowNode? MatchAndOperation(TriggerCaptureContent content, out TemplateMatchResult? result);
}

/// <summary>
/// <see cref="IWorkflowNode"/> 的抽象基类:提供 <see cref="MatchAndOperation"/> 默认实现 + retry counter。
///
/// 为什么不放默认接口方法:C# 8+ 默认接口方法拿不到每个实例的 backing state,
/// retry counter 是实例字段,放接口里会让每个具体类重复写。
///
/// retry 语义(跨调用累计,实例持久):
/// - miss → RetryCount++,RetryCount &lt; MaxRetries 时返回 this,达到 MaxRetries 返回
///   <see cref="FailTemplate"/>(默认 null = 终止工作流)
/// - hit → RetryCount = 0,Operation 执行完后返回 SuccessTemplate(null = 工作流结束)
/// - Operation 抛异常 → 自然上浮给 caller,counter 不变
/// </summary>
public abstract class WorkflowNodeBase : IWorkflowNode
{
    public abstract string Label { get; }
    public abstract int MaxRetries { get; internal set; }
    public int RetryCount { get; private set; }
    public abstract IWorkflowNode? SuccessTemplate { get; internal set; }
    public abstract IWorkflowNode? FailTemplate { get; internal set; }

    public abstract bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result);
    public abstract Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }
    public abstract void Dispose();

    /// <summary>
    /// 画框用的 overlay(由 trigger 在构造节点时传入)。非 null 时,base class 在命中后、
    /// Operation 前自动在 overlay 上画命中框。
    /// null 时跳过画框 —— 测试 mock 或不需要调试可见性的场景使用。
    /// </summary>
    public TriggerOverlayWindow? Overlay { get; }

    protected WorkflowNodeBase(TriggerOverlayWindow? overlay = null)
    {
        Overlay = overlay;
    }

    public IWorkflowNode? MatchAndOperation(TriggerCaptureContent content, out TemplateMatchResult? result)
    {
        bool hit = TryMatch(content, out result);
        if (!hit)
        {
            RetryCount++;
            return RetryCount >= MaxRetries ? FailTemplate : this;
        }
        RetryCount = 0;
        // 命中时统一画框(overlay 非 null 且 result 非 null 才画)
        if (Overlay != null && result != null)
        {
            var abs = result.AbsRect;
            TriggerOverlayWindow.DrawMatchRect(
                Overlay,
                new System.Drawing.Rectangle(abs.X, abs.Y, abs.Width, abs.Height),
                content,
                $"{Label} hit {result.Score:F3}");
        }
        Operation(content, result);
        return SuccessTemplate;
    }
}
