using System;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 工作流节点:检测 + 重试 + 命中动作 + 下一节点四件套。
/// 由 <see cref="WorkflowRunner"/> 在共享 capture 预算内驱动,任一帧命中即执行 Operation 并切换到 SuccessTemplate;
/// 连续 miss 到 MaxRetries 返回 null 终止工作流。
///
/// 与 <see cref="IZzzWaitTarget"/> 并存(后者用于 CommonZzzTaskTrigger 的 delegate-onHit 模式);本接口
/// 是 TestZzzTaskTrigger 21 段硬编码序列的下一代替代。
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
    /// 在给定 capture 帧上尝试匹配。
    /// 命中返回 true,模板命中时 <paramref name="result"/> 非 null。
    /// 内部实现应捕获异常并视为 miss(避免单次抛错打挂整个工作流)。
    /// </summary>
    bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result);

    /// <summary>
    /// 命中后执行的动作(由构造函数或后续 setter 注入的 lambda)。异常自然上浮给 caller。
    /// 属性类型是 <see cref="Action{T1, T2}"/>,可直接像方法一样调用 <c>node.Operation(c, r)</c>。
    /// </summary>
    Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; }

    /// <summary>
    /// 工作流节点的核心入口:单帧 tryMatch + 命中则 Operation + 返回下一节点。
    /// 默认实现在 <see cref="WorkflowNodeBase"/>。
    /// </summary>
    IWorkflowNode? MatchAndOperation(ZzzCaptureContent content, out TemplateMatchResult? result);
}
