# ZZZ IWorkflowNode Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 `IWorkflowNode` + `WorkflowNodeBase` + 3 个具体节点,挂一个用 2 节点验证架构的新 trigger,跑通真实游戏 smoke test。

**Architecture:** 节点组合现有 `IZzzWaitTarget`(不重写检测逻辑),抽象基类 `WorkflowNodeBase` 提供 `MatchAndOperation` 默认逻辑 + retry counter。**没有 `WorkflowRunner`** — dispatcher 50ms timer 已经提供轮询节奏,trigger 持 `_current` 字段,每帧 `OnCapture` 走一步 `MatchAndOperation`。这是中途简化后的设计(原计划里的 `WorkflowRunner` 已在执行中删除,见 Task 4 末尾说明)。

**Tech Stack:** C# / .NET 8 / OpenCvSharp / xUnit (for unit tests)

**Spec:** `Docs/superpowers/specs/2026-09-11-zzz-workflow-node-design.md`

## Global Constraints

- C# language version: C# 12 (项目 `LangVersion` 默认)
- 既有 `IZzzWaitTarget` / `ZzzWaitTarget` / `ZzzWaitForHit` / `CommonZzzTaskTrigger` / `TestZzzTaskTrigger` **完全不动**
- 新文件全部放在 `BetterGenshinImpact/GameTask/Zzz/`(跟现有 ZZZ 文件同一目录)
- 单元测试放 `Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/`
- `IDisposable` 接口由 `IWorkflowNode` 继承(具体类必须实现 `Dispose`)
- 中文注释风格:沿用现有 `ZzzWaitTarget.cs` / `ZzzWaitForHit.cs` 的 `/// <summary>` 风格
- Debug 日志统一前缀 `[ZZZ-Workflow]`(跟现有 `[ZZZ-Wait]` / `[ZZZ-Test]` / `[ZZZ-Common]` 对齐)

---

## File Structure

新建文件:
- `BetterGenshinImpact/GameTask/Zzz/IWorkflowNode.cs` — 接口
- `BetterGenshinImpact/GameTask/Zzz/WorkflowNodeBase.cs` — 抽象类 + 默认 MatchAndOperation
- `BetterGenshinImpact/GameTask/Zzz/TemplateWorkflowNode.cs` — 组合 `TemplateWaitTarget`
- `BetterGenshinImpact/GameTask/Zzz/HsvWorkflowNode.cs` — 组合 `HsvWaitTarget`
- `BetterGenshinImpact/GameTask/Zzz/OcrWorkflowNode.cs` — 组合 `OcrWaitTarget`
- `BetterGenshinImpact/GameTask/Zzz/NewZzzTaskTrigger.cs` — 验证 trigger
- `Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/TestWorkflowNode.cs` — 测试用 mock 节点
- `Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/WorkflowNodeBaseTests.cs` — MatchAndOperation 单元测试

中途删除(原计划有 `WorkflowRunner` 但实现时发现不需要):
- ~~`BetterGenshinImpact/GameTask/Zzz/WorkflowRunner.cs`~~
- ~~`Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/WorkflowRunnerTests.cs`~~

修改文件:
- `BetterGenshinImpact/GameTask/Zzz/ZzzTaskTriggerDispatcher.cs` — 加 `CreateNewTrigger()` + `SetNewTriggerEnabled(bool)`

---

## Task 1: IWorkflowNode 接口

**Files:**
- Create: `BetterGenshinImpact/GameTask/Zzz/IWorkflowNode.cs`

**Interfaces:**
- Consumes: 无
- Produces: `IWorkflowNode` 接口(给后续 task 继承/实现)

- [ ] **Step 1: 创建接口文件**

写 `IWorkflowNode.cs`,内容(参考既有 `IZzzWaitTarget` 的注释风格):

```csharp
using System;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 工作流节点:检测 + 重试 + 命中动作 + 下一节点四件套。
/// 由 trigger(每帧 OnCapture)驱动一轮 <see cref="MatchAndOperation"/>,命中即执行 Operation 并切换到 SuccessTemplate;
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
    /// 命中后执行的动作(由构造函数注入的 lambda)。异常自然上浮给 caller。
    /// </summary>
    void Operation(ZzzCaptureContent content, TemplateMatchResult? result);

    /// <summary>
    /// 工作流节点的核心入口:单帧 tryMatch + 命中则 Operation + 返回下一节点。
    /// 默认实现在 <see cref="WorkflowNodeBase"/>。
    /// </summary>
    IWorkflowNode? MatchAndOperation(ZzzCaptureContent content, out TemplateMatchResult? result);
}
```

- [ ] **Step 2: 编译验证**

跑(在 `BetterGenshinImpact/` 目录下):
```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug
```
预期:编译通过(目前接口没实现,只要引用方不存在就 OK)。

- [ ] **Step 3: 提交**

```bash
git add BetterGenshinImpact/GameTask/Zzz/IWorkflowNode.cs
git commit -m "feat(zzz): add IWorkflowNode interface for workflow node abstraction"
```

---

## Task 2: WorkflowNodeBase + MatchAndOperation 单元测试(TDD)

**Files:**
- Create: `Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/TestWorkflowNode.cs`
- Create: `Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/WorkflowNodeBaseTests.cs`
- Create: `BetterGenshinImpact/GameTask/Zzz/WorkflowNodeBase.cs`

**Interfaces:**
- Consumes: `IWorkflowNode` (Task 1)
- Produces: `WorkflowNodeBase` 抽象类 + 默认 `MatchAndOperation` 实现;`TestWorkflowNode` 测试用 mock

- [ ] **Step 1: 写测试用 mock 节点 `TestWorkflowNode.cs`**

```csharp
using System;
using BetterGenshinImpact.GameTask.Zzz;

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
        bool operationThrows = false)
    {
        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _tryMatchResult = tryMatchResult;
        _tryMatchOut = tryMatchOut;
        _operationThrows = operationThrows;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; }

    public int OperationCallCount { get; private set; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
    {
        result = _tryMatchOut;
        return _tryMatchResult;
    }

    public override void Operation(ZzzCaptureContent content, TemplateMatchResult? result)
    {
        OperationCallCount++;
        if (_operationThrows)
        {
            throw new InvalidOperationException("test operation failure");
        }
    }

    public override void Dispose() { }
}
```

- [ ] **Step 2: 写失败的 MatchAndOperation 单元测试 `WorkflowNodeBaseTests.cs`**

```csharp
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
        var node = new TestWorkflowNode("hit", maxRetries: 3, successTemplate: next,
            tryMatchResult: true);

        // 模拟"之前 miss 2 次"的脏状态
        var content = MakeFakeContent();
        node.MatchAndOperation(content, out _);
        node.MatchAndOperation(content, out _);
        Assert.Equal(2, node.RetryCount);

        // hit → reset + return next
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
    public void MatchAndOperation_MissThenHit_RetryCountResets()
    {
        var node = new TestWorkflowNode("mixed", maxRetries: 5, successTemplate: null,
            tryMatchResult: false);

        var content = MakeFakeContent();
        node.MatchAndOperation(content, out _);
        node.MatchAndOperation(content, out _);
        Assert.Equal(2, node.RetryCount);

        // 改 hit 行为:用一个新节点(因为 mock 的 tryMatch 是构造时定死的)
        var hitNode = new TestWorkflowNode("hit", maxRetries: 5, successTemplate: null,
            tryMatchResult: true);

        var result = hitNode.MatchAndOperation(content, out _);
        Assert.Null(result); // SuccessTemplate = null
        Assert.Equal(0, hitNode.RetryCount);
    }
}
```

- [ ] **Step 3: 跑测试,确认失败(WorkflowNodeBase 还不存在)**

跑:
```bash
dotnet test Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj --filter "FullyQualifiedName~WorkflowNodeBaseTests"
```
预期:编译失败,提示 `WorkflowNodeBase` 不存在。

- [ ] **Step 4: 写 `WorkflowNodeBase.cs` 抽象类**

```csharp
using System;

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
    public abstract IWorkflowNode? SuccessTemplate { get; }

    public abstract bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result);
    public abstract void Operation(ZzzCaptureContent content, TemplateMatchResult? result);
    public abstract void Dispose();

    public IWorkflowNode? MatchAndOperation(ZzzCaptureContent content, out TemplateMatchResult? result)
    {
        bool hit = TryMatch(content, out result);
        if (!hit)
        {
            RetryCount++;
            return RetryCount >= MaxRetries ? null : this;
        }
        RetryCount = 0;
        Operation(content, result);
        return SuccessTemplate;
    }
}
```

- [ ] **Step 5: 跑测试,确认全过**

跑:
```bash
dotnet test Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj --filter "FullyQualifiedName~WorkflowNodeBaseTests"
```
预期:5 个测试全过。

- [ ] **Step 6: 提交**

```bash
git add BetterGenshinImpact/GameTask/Zzz/WorkflowNodeBase.cs \
        Test/BetterGenshinImpact.UnitTest/GameTaskTests/ZzzTests/
git commit -m "feat(zzz): add WorkflowNodeBase + MatchAndOperation default impl with retry counter"
```

---

## Task 3: TemplateWorkflowNode / HsvWorkflowNode / OcrWorkflowNode

**Files:**
- Create: `BetterGenshinImpact/GameTask/Zzz/TemplateWorkflowNode.cs`
- Create: `BetterGenshinImpact/GameTask/Zzz/HsvWorkflowNode.cs`
- Create: `BetterGenshinImpact/GameTask/Zzz/OcrWorkflowNode.cs`

**Interfaces:**
- Consumes: `WorkflowNodeBase` (Task 2), `IZzzWaitTarget` 的三个具体类(已有,不重写)
- Produces: 3 个组合具体类,每个都是 `WorkflowNodeBase` 子类

- [ ] **Step 1: 写 `TemplateWorkflowNode.cs`**

```csharp
using System;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 模板匹配工作流节点:组合 <see cref="TemplateWaitTarget"/>,检测逻辑完全复用(懒加载 + score ≥ threshold 命中)。
/// 命中帧上的 ROI 画框 / 点击坐标由 <paramref name="operation"/> lambda 负责(可调 <see cref="ZzzTriggerActions"/>)。
/// </summary>
public sealed class TemplateWorkflowNode : WorkflowNodeBase
{
    private readonly TemplateWaitTarget _target;
    private readonly Action<ZzzCaptureContent, TemplateMatchResult?> _operation;

    public TemplateWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        string templatePath,
        double threshold,
        Action<ZzzCaptureContent, TemplateMatchResult?> operation)
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
        _operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Operation(ZzzCaptureContent content, TemplateMatchResult? result)
        => _operation(content, result);

    public override void Dispose() => _target.Dispose();
}
```

- [ ] **Step 2: 写 `HsvWorkflowNode.cs`**

```csharp
using System;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// HSV / 任意 detector 工作流节点:组合 <see cref="HsvWaitTarget"/>,detector 抛异常被内部吞掉(避免打挂工作流)。
/// 命中时 TemplateMatchResult 为 null(operation 走 ROI 画框 / 固定点击区路径)。
/// </summary>
public sealed class HsvWorkflowNode : WorkflowNodeBase
{
    private readonly HsvWaitTarget _target;
    private readonly Action<ZzzCaptureContent, TemplateMatchResult?> _operation;

    public HsvWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        Func<ZzzCaptureContent, bool> detector,
        Action<ZzzCaptureContent, TemplateMatchResult?> operation)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(operation);

        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _target = new HsvWaitTarget(label, detector);
        _operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Operation(ZzzCaptureContent content, TemplateMatchResult? result)
        => _operation(content, result);

    public override void Dispose() { /* HsvWaitTarget 不持资源 */ }
}
```

- [ ] **Step 3: 写 `OcrWorkflowNode.cs`**

```csharp
using System;
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
    private readonly Action<ZzzCaptureContent, TemplateMatchResult?> _operation;

    public OcrWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        CvRect roi,
        string targetText,
        Action<ZzzCaptureContent, TemplateMatchResult?> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Label = label;
        MaxRetries = maxRetries;
        SuccessTemplate = successTemplate;
        _target = new OcrWaitTarget(label, roi, targetText);
        _operation = operation;
    }

    public override string Label { get; }
    public override int MaxRetries { get; }
    public override IWorkflowNode? SuccessTemplate { get; }

    public override bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result)
        => _target.TryMatch(content, out result);

    public override void Operation(ZzzCaptureContent content, TemplateMatchResult? result)
        => _operation(content, result);

    public override void Dispose() { /* OcrWaitTarget 不持资源 */ }
}
```

- [ ] **Step 4: 编译验证**

跑:
```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug
```
预期:编译通过(具体类暂未被使用,只要签名正确就行)。

- [ ] **Step 5: 提交**

```bash
git add BetterGenshinImpact/GameTask/Zzz/TemplateWorkflowNode.cs \
        BetterGenshinImpact/GameTask/Zzz/HsvWorkflowNode.cs \
        BetterGenshinImpact/GameTask/Zzz/OcrWorkflowNode.cs
git commit -m "feat(zzz): add Template/Hsv/OcrWorkflowNode concrete classes wrapping existing IZzzWaitTarget"
```

---

## Task 4: WorkflowRunner — 中途删除

**Status: 取消。** 原计划是在 Task 4 实现 `WorkflowRunner` 静态类(同步阻塞 capture 循环),但执行到一半时发现不需要:

- `ZzzTaskTriggerDispatcher` 已有 50ms timer 在轮询,每帧调 `trigger.OnCapture(content)` 把 frame 喂给 trigger
- trigger 自己再起一个 `Thread.Sleep` 循环会阻塞几秒,把整个 dispatcher 的其他 trigger 都饿死
- 语义跟现有 `TestZzzTaskTrigger` 调 `ZzzWaitForHit.Run` 一样冗余(那也是阻塞循环)

简化方案(已实现):trigger 持 `IWorkflowNode? _current` 字段,每帧 `OnCapture` 走一步 `_current.MatchAndOperation(content)`:

- `null` → 工作流结束,trigger 后续 OnCapture 早返回
- `== current` → miss,留在原节点
- `!= current` → advance 到下一节点

`dispatcher.Tick` 已经在 50ms 节奏上跑,自然就是轮询。trigger 不再需要 `totalTimeoutMs` / `intervalMs` / `captureProvider` 参数 — frame 由 dispatcher 提供。

`WorkflowRunner.cs` 和 `WorkflowRunnerTests.cs` 已经删除。如果未来 Phase 2 / Phase 3 需要 batch / 离线触发(没有 dispatcher 的场景),可以再重新引入这个类。

---

## Task 5: NewZzzTaskTrigger(2-3 节点验证 trigger)

**Files:**
- Create: `BetterGenshinImpact/GameTask/Zzz/NewZzzTaskTrigger.cs`

**Interfaces:**
- Consumes: `IZzzTaskTrigger` (已有), `IWorkflowNode` (Task 1), `TemplateWorkflowNode`/`HsvWorkflowNode` (Task 3), `ZzzTriggerActions` (已有), `ZzzCaptureContent` (已有)
- Produces: `NewZzzTaskTrigger` 类 — 持 `_current` 字段,每帧 OnCapture 走一步 MatchAndOperation

**节点链设计**(简化版,只验证架构):
```
Node 1: TemplateWorkflowNode(F2 模板,maxRetries=2,SuccessTemplate=Node 2)
  Operation: PressKeyForeground(VK_F2, content)

Node 2: HsvWorkflowNode(ContinueArrow HSV detector,maxRetries=2,SuccessTemplate=null)
  Operation: PressKeyForeground(VK_SPACE, content)
```

意图:覆盖模板节点 + HSV 节点 + advance + terminate 三种场景。

- [ ] **Step 1: 写 `NewZzzTaskTrigger.cs`**

```csharp
using System;
using System.Diagnostics;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// Phase 1 验证 trigger:用 2 节点的 IWorkflowNode 链验证新架构。
///
/// 节点链:
/// <list type="number">
///   <item>Node 1: TemplateWorkflowNode — 等 F2 模板命中 → 按 F2 → advance 到 Node 2</item>
///   <item>Node 2: HsvWorkflowNode — 等 ContinueArrow(右下角 HSV) → 按 Space → terminate</item>
/// </list>
///
/// 每帧一次 OnCapture = 工作流走一步:_current.MatchAndOperation(content) → 返回
/// 下一节点 / null / 自身。Dispatcher 50ms timer 自然提供轮询节奏,不再需要内部 sleep。
///
/// 行为对齐 <see cref="TestZzzTaskTrigger.RunSeqF2"/> + <see cref="TestZzzTaskTrigger.RunSeqContinueArrow"/>
/// 的最小子集,只用来证明 IWorkflowNode + dispatcher-timer-as-poll 跑得通,不带 retreat/scan 兜底。
///
/// Phase 2 会用同样的模式把 TestZzzTaskTrigger 的 21 段全部拆成节点链,本 trigger 那时会被删掉。
/// </summary>
public sealed class NewZzzTaskTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "NewZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    private readonly ZzzOverlayWindow? _overlay;
    private IWorkflowNode? _current;

    private const double MatchThreshold = 0.96;
    private const int MaxRetries = 2;

    private const string F2Path = "Assets\\Template\\Daily\\daliyF2_1538_116_36x24_200.png";

    // 继续对话箭头 HSV 参数(与 TestZzzTaskTrigger 完全一致)
    private static readonly Rect ContinueArrowRoi = new(1462, 966, 42, 45);
    private const int ContinueArrowSMax = 40;
    private const int ContinueArrowVMin = 130;
    private const int ContinueArrowMinArea = 80;
    private const int ContinueArrowMaxArea = 400;
    private const double ContinueArrowMinAspect = 1.3;
    private const double ContinueArrowMinExtent = 0.4;
    private const double ContinueArrowMinLargestFraction = 0.5;
    private const double ContinueArrowMaxConvexity = 0.85;
    private const int ContinueArrowMinHeight = 16;
    private const int ContinueArrowMaxLabels = 2;

    public NewZzzTaskTrigger(ZzzOverlayWindow? overlay = null)
    {
        _overlay = overlay;

        // 先构造 Node 2(ContinueArrow),Node 1.SuccessTemplate 引用它
        var node2 = new HsvWorkflowNode(
            label: "NewSeq1/ContinueArrow",
            maxRetries: MaxRetries,
            successTemplate: null, // 工作流结束
            detector: c => ZzzImageUtils.DetectArrowBlob(
                c.Image, ContinueArrowRoi,
                ContinueArrowSMax, ContinueArrowVMin,
                ContinueArrowMinArea, ContinueArrowMaxArea,
                ContinueArrowMinHeight,
                ContinueArrowMinAspect, ContinueArrowMinExtent,
                ContinueArrowMinLargestFraction, ContinueArrowMaxConvexity,
                ContinueArrowMaxLabels),
            operation: (content, _) =>
            {
                Debug.WriteLine("[ZZZ-Workflow-New] Node2 hit → press SPACE");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_SPACE, content);
            });

        var node1 = new TemplateWorkflowNode(
            label: "NewSeq0/F2",
            maxRetries: MaxRetries,
            successTemplate: node2,
            templatePath: F2Path,
            threshold: MatchThreshold,
            operation: (content, result) =>
            {
                Debug.WriteLine("[ZZZ-Workflow-New] Node1 hit → press F2");
                if (result != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, result, content, "NewSeq0/F2");
                }
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F2, content);
            });

        _current = node1;
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        var current = _current;
        if (current == null)
        {
            return;
        }

        IWorkflowNode? next;
        try
        {
            next = current.MatchAndOperation(content, out _);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Workflow-New] node={current.Label} threw: {ex.GetType().Name}: {ex.Message}");
            next = null;
        }

        if (next == null)
        {
            Debug.WriteLine($"[ZZZ-Workflow-New] node={current.Label} → terminate");
        }
        else if (next != current)
        {
            Debug.WriteLine($"[ZZZ-Workflow-New] node={current.Label} → advance to {next.Label}");
        }
        else
        {
            Debug.WriteLine($"[ZZZ-Workflow-New] node={current.Label} miss → stay (retry {current.RetryCount}/{current.MaxRetries})");
        }

        _current = next;
    }

    public void Dispose()
    {
        _current?.Dispose();
    }
}
```

- [ ] **Step 2: 编译验证**

跑:
```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug
```
预期:编译通过。

- [ ] **Step 3: 提交**

```bash
git add BetterGenshinImpact/GameTask/Zzz/NewZzzTaskTrigger.cs
git commit -m "feat(zzz): add NewZzzTaskTrigger with 2-node chain for architecture validation"
```

---

## Task 6: 把 NewZzzTaskTrigger 接入 dispatcher

**Files:**
- Modify: `BetterGenshinImpact/GameTask/Zzz/ZzzTaskTriggerDispatcher.cs`

**Interfaces:**
- Consumes: `NewZzzTaskTrigger` (Task 5)
- Produces: `CreateNewTrigger()` 方法 + `SetNewTriggerEnabled(bool)` hot-toggle

- [ ] **Step 1: 加 `CreateNewTrigger()` 私有方法**

读 `ZzzTaskTriggerDispatcher.cs` 现有代码(在 `CreateCommonTrigger()` 后面追加):

```csharp
private NewZzzTaskTrigger CreateNewTrigger()
{
    // 不传 captureProvider:trigger 直接用 dispatcher OnCapture 喂进来的 content 走一步。
    return new NewZzzTaskTrigger(overlay: _overlay);
}
```

- [ ] **Step 2: 在 `Start()` 加 runNewTrigger 参数**

修改 `Start()` 签名(沿用 `runTestTrigger` / `runCommonTrigger` 模式):

```csharp
public void Start(nint hWnd, CaptureModes mode,
    bool runTestTrigger = false,
    bool runCommonTrigger = false,
    bool runNewTrigger = false)
{
    // ...
    if (runNewTrigger)
    {
        triggers.Add(CreateNewTrigger());
    }
    // ...
}
```

- [ ] **Step 3: 加 `SetNewTriggerEnabled(bool)` hot-toggle**

参考 `SetCommonTriggerEnabled` 实现(在它后面追加):

```csharp
/// <summary>
/// 热挂载/卸载新架构 trigger:用 IWorkflowNode 链验证用,Phase 1 临时开关。
/// </summary>
public void SetNewTriggerEnabled(bool enabled)
{
    lock (_locker)
    {
        if (_capture == null)
        {
            return;
        }

        var hasNew = _triggers.Exists(t => t is NewZzzTaskTrigger);
        if (enabled == hasNew)
        {
            return;
        }

        var newList = new List<IZzzTaskTrigger>(_triggers);
        if (enabled)
        {
            newList.Add(CreateNewTrigger());
            Debug.WriteLine("[ZZZ] new trigger 已挂载(重新打开)");
        }
        else
        {
            var newTrig = newList.Find(t => t is NewZzzTaskTrigger);
            if (newTrig != null)
            {
                newList.Remove(newTrig);
                (newTrig as IDisposable)?.Dispose();
            }
            Debug.WriteLine("[ZZZ] new trigger 已卸载");
        }
        _triggers = newList;
    }
}
```

- [ ] **Step 4: 编译验证**

跑:
```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug
```
预期:编译通过。

- [ ] **Step 5: 提交**

```bash
git add BetterGenshinImpact/GameTask/Zzz/ZzzTaskTriggerDispatcher.cs
git commit -m "feat(zzz): wire NewZzzTaskTrigger into dispatcher with hot-toggle support"
```

---

## Task 7: 真实游戏 smoke test

**Files:** 不修改任何代码,纯手动验证

**验证步骤:**

- [ ] **Step 1: 构建 Release 版本**

跑:
```bash
dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Release
```
预期:无 error。可能有的 warning 是已知项,与本次改动无关。

- [ ] **Step 2: 启动游戏 + 应用**

参考记忆里的"改完代码→先停VS调试→再杀旧BetterGI进程→最后启动新实例":
1. 停掉 VS 调试
2. 任务管理器杀光 `BetterGenshinImpact.exe` 老进程
3. 启动刚 build 的 BetterGI 实例
4. 在应用里启 ZZZ 子系统(切到 ZZZ 标签页)

- [ ] **Step 3: 临时启用 NewZzzTaskTrigger**

由于本 Phase 没加 UI 开关,在代码里临时启用:
- 找到 ViewModel 里调 `Start()` 的位置(或 `SetCommonTriggerEnabled` 之类),临时把 `runNewTrigger: true` 加上,或者调一次 `SetNewTriggerEnabled(true)`
- 跑一次重新 build

或者:在应用代码里临时塞一个启动后立即调 `dispatcher.SetNewTriggerEnabled(true)` 的钩子。

- [ ] **Step 4: 跑日常任务流程**

游戏里跑一遍日常流程:
- 进入 F2 界面 → 应该看到 trigger 自动按 F2(F2 模板命中 → Operation 按 F2 键 → advance 到 Node 2)
- 弹出对话框 → 应该看到 trigger 按 Space(ContinueArrow HSV 命中 → Operation 按 Space → terminate)
- 工作流结束

预期 Debug 输出(可在 VS 调试窗口或 DebugView 里看到):
```
[ZZZ-Workflow] iter=1 node=NewSeq0/F2 miss → stay (retry 1/2)
[ZZZ-Workflow] iter=2 node=NewSeq0/F2 hit → advance to NewSeq1/ContinueArrow
[ZZZ-Workflow] iter=3 node=NewSeq1/ContinueArrow hit → terminate
[ZZZ-Workflow-New] Node1 hit → press F2
[ZZZ-Workflow-New] Node2 hit → press SPACE
```

- [ ] **Step 5: 失败路径验证**

把 `MaxRetries` 临时调成 `1`,然后故意把 F2 模板路径改错(比如指向不存在的 png):
- 跑一遍,期望看到 `iter=1 miss → stay (retry 1/1)` 然后 `terminate` 退出
- 验证工作流不会无限挂住

- [ ] **Step 6: 回滚临时改动**

完成 smoke test 后,把临时改的 `MaxRetries` / 模板路径 / UI 钩子全部回滚。

- [ ] **Step 7: 提交(如有遗留)**

如果有回滚不到位的改动:
```bash
git add -u
git status
# 确认 diff 合理后再 commit
git commit -m "chore(zzz): post-smoke-test cleanup"
```

---

## Self-Review

### Spec coverage

Spec 要求 vs plan task 映射:

| Spec 要求 | Plan task |
|-----------|-----------|
| `IWorkflowNode` 接口 | Task 1 |
| `WorkflowNodeBase` 抽象类 + 默认 MatchAndOperation + retry counter | Task 2 |
| `TemplateWorkflowNode` | Task 3 |
| `HsvWorkflowNode` | Task 3 |
| `OcrWorkflowNode` | Task 3 |
| ~~`WorkflowRunner` 静态类~~ | Task 4 取消 |
| `NewZzzTaskTrigger` 验证用 | Task 5 |
| `ZzzTaskTriggerDispatcher` 加 `CreateNewTrigger` + `SetNewTriggerEnabled` | Task 6 |
| 真实游戏 smoke test | Task 7 |

7/7 覆盖(原 8/8,中途简化取消 `WorkflowRunner`)。

### Placeholder scan

无 "TBD" / "TODO" / "implement later"。Task 7 的临时改动明确说了回滚。

### Type consistency

- `IWorkflowNode.Label` / `MaxRetries` / `RetryCount` / `SuccessTemplate` / `TryMatch` / `Operation` / `MatchAndOperation` 在 Task 1 定义,Task 2-5 全部对齐
- `WorkflowNodeBase` 在 Task 2 定义,Task 3 具体类继承,Task 5 trigger 用 — 签名一致
- `TestWorkflowNode` (Task 2) 继承 `WorkflowNodeBase`,签名一致
- `NewZzzTaskTrigger` 在 Task 5 直接持 `_current` 字段调 `MatchAndOperation`(没有 `WorkflowRunner` 中介),签名一致

### Ambiguity check

- Task 5 的 `OnCapture` 直接用 dispatcher 喂进来的 `content`,每帧走一步 — 不再有 `captureProvider` 参数
- Task 5 的 `_current` 是 `IWorkflowNode?`(`Dispose` 通过接口继承的 `IDisposable` 直接调)
- Task 7 的 UI 钩子位置依赖具体 ViewModel 实现,plan 里给了指引但不强求具体行号 — 工程师需要自己定位

无重大歧义。
