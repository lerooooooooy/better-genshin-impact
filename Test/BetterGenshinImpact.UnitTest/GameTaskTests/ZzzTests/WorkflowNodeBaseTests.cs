using System;
using BetterGenshinImpact.GameTask.Zzz;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ZzzTests;

public class WorkflowNodeBaseTests
{
    private static ZzzCaptureContent MakeFakeContent()
    {
        // 1x1 灰度 Mat,纯用于满足 ZzzCaptureContent 构造参数。
        var mat = new Mat(1, 1, MatType.CV_8UC3, Scalar.All(128));
        return new ZzzCaptureContent(mat, 0, 100.0, IntPtr.Zero);
    }

    [Fact]
    public void MatchAndOperation_Hit_ResetsRetryCounterAndReturnsSuccessTemplate()
    {
        var next = new TestWorkflowNode("next", 3, null, tryMatchResult: true);

        // 用一个 miss 节点先跑出脏状态(retry counter 是实例字段,跟"是哪个节点"无关)
        var missNode = new TestWorkflowNode("miss", maxRetries: 10, successTemplate: null,
            tryMatchResult: false);
        var content = MakeFakeContent();
        missNode.MatchAndOperation(content, out _);
        missNode.MatchAndOperation(content, out _);
        Assert.Equal(2, missNode.RetryCount);

        // 现在测 hit 节点:从 0 开始,hit 后仍为 0 + return next
        var node = new TestWorkflowNode("hit", maxRetries: 3, successTemplate: next,
            tryMatchResult: true);
        var result = node.MatchAndOperation(content, out _);
        Assert.Same(next, result);
        Assert.Equal(0, node.RetryCount);
        Assert.Equal(1, node.OperationCallCount);
    }

    [Fact]
    public void MatchAndOperation_Miss_IncrementsCounterAndReturnsSelf()
    {
        var node = new TestWorkflowNode("miss", maxRetries: 5, successTemplate: null,
            tryMatchResult: false);

        var content = MakeFakeContent();
        var result = node.MatchAndOperation(content, out _);

        Assert.Same(node, result);
        Assert.Equal(1, node.RetryCount);
    }

    [Fact]
    public void MatchAndOperation_MissMaxRetries_ReturnsNull()
    {
        var node = new TestWorkflowNode("max", maxRetries: 3, successTemplate: null,
            tryMatchResult: false);

        var content = MakeFakeContent();
        var r1 = node.MatchAndOperation(content, out _);
        var r2 = node.MatchAndOperation(content, out _);
        var r3 = node.MatchAndOperation(content, out _);

        Assert.Same(node, r1);
        Assert.Same(node, r2);
        Assert.Null(r3);
        Assert.Equal(3, node.RetryCount);
    }

    [Fact]
    public void MatchAndOperation_OperationThrows_PropagatesException()
    {
        var node = new TestWorkflowNode("throw", maxRetries: 3, successTemplate: null,
            tryMatchResult: true, operationThrows: true);

        var content = MakeFakeContent();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            node.MatchAndOperation(content, out _));
        Assert.Equal("test operation failure", ex.Message);
    }

    [Fact]
    public void MatchAndOperation_HitOnFreshNode_RetryCountStartsAtZero()
    {
        var node = new TestWorkflowNode("fresh", maxRetries: 5, successTemplate: null,
            tryMatchResult: true);

        var content = MakeFakeContent();
        var result = node.MatchAndOperation(content, out _);

        Assert.Null(result); // SuccessTemplate = null
        Assert.Equal(0, node.RetryCount);
        Assert.Equal(1, node.OperationCallCount);
    }
}
