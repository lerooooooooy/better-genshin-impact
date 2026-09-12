using System;
using BetterGenshinImpact.GameTask.Zzz;
using BetterGenshinImpact.View.Windows;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ZzzTests;

/// <summary>
/// 测试用 mock 节点:可预设 TryMatch 返回值 + Operation 抛不抛。
/// </summary>
internal sealed class TestWorkflowNode : WorkflowNodeBase
{
    private readonly bool _tryMatchResult;
    private readonly TemplateMatchResult? _tryMatchOut;
    private readonly bool _operationThrows;

    public TestWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        bool tryMatchResult,
        TemplateMatchResult? tryMatchOut = null,
        bool operationThrows = false,
        ZzzOverlayWindow? overlay = null)
        : base(overlay)
    {
        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _tryMatchResult = tryMatchResult;
        _tryMatchOut = tryMatchOut;
        _operationThrows = operationThrows;
        // Operation 是可赋值属性,把计数+抛错的逻辑放进注入的 lambda,
        // 这样 ctor 调用方就能预设行为,不再需要 override 方法。
        Operation = (_, _) =>
        {
            OperationCallCount++;
            if (_operationThrows)
            {
                throw new InvalidOperationException("test operation failure");
            }
        };
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; internal set; }
    public override Action<ZzzCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public int OperationCallCount { get; private set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
    {
        result = _tryMatchOut;
        return _tryMatchResult;
    }

    public override void Dispose() { }
}
