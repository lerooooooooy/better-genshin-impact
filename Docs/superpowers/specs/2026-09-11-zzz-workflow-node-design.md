# ZZZ IWorkflowNode 架构整改 — 设计 Spec

**Date**: 2026-09-11
**Status**: Draft
**Scope**: `BetterGenshinImpact/GameTask/Zzz/` 子系统

## 背景

当前 ZZZ 子系统有两类 trigger:

- `TestZzzTaskTrigger`:21 段硬编码序列,每段调 `ZzzWaitForHit.Run(...)` 等模板命中后执行段专属动作(`PressKeyForeground` / `ClickMatchedArea` / `ClickRect` 等)。失败走 retreat/scan 兜底。
- `CommonZzzTaskTrigger`:对 20 个主模板单帧扫描,首命中执行对应动作,下一帧重新全扫描。无序列逻辑。

两者的共同抽象是 `IZzzWaitTarget` (见 `ZzzWaitTarget.cs`) — 纯检测接口(`TryMatch` + `Label` + `Threshold`),由 `ZzzWaitForHit` 工具包成轮询循环。

痛点:
1. **序列逻辑全在 trigger 里**:21 段几乎完全相同 — 等目标、命中、做动作、推进。`TestZzzTaskTrigger.cs` 900+ 行,90% 是这种重复样板。
2. **失败/重试/序列推进散落在 trigger**:retry / retreat / scan 兜底都是 trigger 私有状态机,无法被新 trigger 复用。
3. **复合操作和简单操作共用一套机制**:RunSeqDailySel 在同一节点里做 daliyUi + DailySel/NoSel + DaliyFin/Reach 4 个匹配,逻辑拥挤。
4. **添加新 trigger 门槛高**:新 trigger 必须复刻 capture/timeout/retreat/scan 一整套基础设施。

## 目标

引入 `IWorkflowNode` 抽象,把"检测 + 重试 + 命中动作 + 下一节点"打包成一个节点类型。新 trigger 由 `IWorkflowNode` 链 + 一个通用 runner 组成,不再需要每个 trigger 自己实现状态机。

## 非目标

- 不迁移 `CommonZzzTaskTrigger` 到新架构(后续单独 spec)
- 不删除 `IZzzWaitTarget` / `ZzzWaitTarget` / `ZzzWaitForHit`(Common trigger 继续用)
- 不重写检测逻辑(`TemplateWaitTarget` 的懒加载、`HsvWaitTarget` 的 detector delegate、`OcrWaitTarget` 的 PaddleOCR 调用都保留,新节点组合复用)
- 不改 `ZzzTaskTriggerDispatcher` 接入逻辑(新 trigger 通过现有的 `Add/SetTestTriggerEnabled` 路径接入)

## 架构

### 组件清单

| 类型 | 文件 | 角色 |
|------|------|------|
| `IWorkflowNode` (接口) | 新建 `IWorkflowNode.cs` | 节点契约 |
| `WorkflowNodeBase` (抽象类) | 新建 `WorkflowNodeBase.cs` | 默认 `MatchAndOperation` 实现 + retry counter |
| `TemplateWorkflowNode` | 新建 `TemplateWorkflowNode.cs` | 组合 `TemplateWaitTarget` |
| `HsvWorkflowNode` | 新建 `HsvWorkflowNode.cs` | 组合 `HsvWaitTarget` |
| `OcrWorkflowNode` | 新建 `OcrWorkflowNode.cs` | 组合 `OcrWaitTarget` |
| `NewZzzTaskTrigger` (新 trigger) | 新建 `NewZzzTaskTrigger.cs` | trigger 持 `_current` 字段,每帧 OnCapture 走一步 |
| `ZzzTaskTriggerDispatcher` | 修改 | 新增 `CreateNewTrigger()` + `SetNewTriggerEnabled(bool)`,类似现有 `SetTestTriggerEnabled` |

注:**没有 `WorkflowRunner`**。原计划里有,但实现时简化了 —— dispatcher 50ms timer 已经在轮询,trigger 没必要再起一个内部 while 循环。轮询节奏 = dispatcher tick 频率,trigger `OnCapture` 调一次 `_current.MatchAndOperation(content)` 即可推进工作流。

### IWorkflowNode 接口

```csharp
public interface IWorkflowNode : IDisposable
{
    string Label { get; }
    int MaxRetries { get; }
    int RetryCount { get; }                            // 当前失败计数(只读,日志用)
    IWorkflowNode? SuccessTemplate { get; }            // 命中后下一节点;null = 工作流结束

    bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? result);
    void Operation(ZzzCaptureContent content, TemplateMatchResult? result);
    IWorkflowNode? MatchAndOperation(ZzzCaptureContent content, out TemplateMatchResult? result);
}
```

### WorkflowNodeBase 抽象类

提供 `MatchAndOperation` 默认实现 + retry counter 实例状态:

```csharp
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

**为什么用抽象基类而不是默认接口方法**:C# 8+ 默认接口方法拿不到每个实例的 backing state。retry counter 是实例状态,放默认接口方法里需要每个具体类自己写一遍,DRY 差。抽象基类一处实现、所有子类继承。

### 具体子类

3 个具体类都组合现有 `IZzzWaitTarget`,不重写检测逻辑:

```csharp
public sealed class TemplateWorkflowNode : WorkflowNodeBase
{
    private readonly TemplateWaitTarget _target;
    public TemplateWorkflowNode(string label, int maxRetries, IWorkflowNode? successTemplate, string templatePath, double threshold)
    {
        _target = new TemplateWaitTarget(label, templatePath, threshold);
        ...
    }
    public override bool TryMatch(ZzzCaptureContent c, out TemplateMatchResult? r) => _target.TryMatch(c, out r);
    public override void Operation(...) { /* 调用方注入的 action */ }
    public override void Dispose() => _target.Dispose();
}
```

`HsvWorkflowNode` / `OcrWorkflowNode` 同模式。

### Operation 注入方式

每个具体类的构造函数接受 `Action<ZzzCaptureContent, TemplateMatchResult?>` 作为必填参数,赋值给 `Operation` override:

```csharp
public sealed class TemplateWorkflowNode : WorkflowNodeBase
{
    private readonly Action<ZzzCaptureContent, TemplateMatchResult?> _operation;

    public TemplateWorkflowNode(
        string label,
        int maxRetries,
        IWorkflowNode? successTemplate,
        string templatePath,
        double threshold,
        Action<ZzzCaptureContent, TemplateMatchResult?> operation)
    {
        _operation = operation;
        ...
    }

    public override void Operation(ZzzCaptureContent c, TemplateMatchResult? r) => _operation(c, r);
}
```

trigger 在构造节点链时一并传入 lambda。`Action` 不可变,避免节点构建后被偷偷改掉。

### Trigger 持有 `_current` 字段(替代原 WorkflowRunner)

最初设计里有一个 `WorkflowRunner` 静态类包 while 循环,实现时发现没必要:
dispatcher 50ms timer 已经在轮询,trigger 再起一个内部 sleep 循环会:
- 每次 `OnCapture` 阻塞几秒,期间其他 trigger 走不动
- 跟现有 `TestZzzTaskTrigger` 调 `ZzzWaitForHit.Run` 的语义重叠

新设计:
- trigger 持有 `IWorkflowNode? _current` 字段,初始化为链头节点
- 每次 `OnCapture(content)` 走一步:`var local = _current; _current = local.MatchAndOperation(content, out _);`
- 返回值约定(per `MatchAndOperation` 默认逻辑):
  - `null` → 工作流结束,trigger 后续 OnCapture 早返回
  - `== current` → miss,留在原节点
  - `!= current` → advance 到下一节点
- 轮询节奏由 dispatcher 50ms timer 提供,trigger 不再需要 `totalTimeoutMs` / `intervalMs` 参数

异常处理:`OnCapture` 顶层 try/catch 兜住 `MatchAndOperation` 异常,记日志 + `_current = null` 终止工作流。

### Retry 语义

- 每次 miss → `RetryCount++`,返回 `this`
- `RetryCount >= MaxRetries` → 返回 `null`,trigger 终止工作流
- 命中 → `RetryCount = 0`,调 Operation,返回 `SuccessTemplate`
- retry counter 是节点实例字段,跨多次 `OnCapture` 调用持续累计

## 数据流

`NewZzzTaskTrigger.OnCapture(content)` 跟现有 trigger 不同 — **不走阻塞循环**,每帧走一步:

```
Dispatcher.Tick (50ms timer)
  ↓ TryEnter(_locker) — 抢锁失败直接丢帧
  ↓ capture (一次)
  ↓ for each trigger:
    trigger.OnCapture(content)  // 单步,~ms 级
        ↓
    if trigger is NewZzzTaskTrigger:
        var local = _current
        try:
            next = local.MatchAndOperation(content, out _)
        except:
            log; next = null
        _current = next
        // next == null → 工作流结束
        // next == local → miss,下次继续
        // next != local → advance
  ↓ content.Dispose() (dispatcher 顶层 using)
```

每次 `OnCapture` 单步开销约几 ms(模板匹配 + 可能的点击/按键),不再阻塞其他 trigger。`ZzzCaptureContent` 生命周期由 dispatcher 负责(创建 + `using` Dispose),trigger 不需要管。

## 错误处理

| 错误 | 处理 |
|------|------|
| `MatchAndOperation` 抛异常 | `OnCapture` 顶层 try/catch 兜住,log + `_current = null` 终止工作流 |
| `Operation` 抛异常 | 自然上浮给 `MatchAndOperation` → trigger 顶层 try/catch 兜住 |
| 模板加载失败(继承自 `TemplateWaitTarget`) | `TryMatch` 返回 false,跟普通 miss 一样累计 retry |
| OCR/PaddleOCR 抛异常 | 同上,`TryMatch` 内 try/catch |

## 测试

实施后必须跑通:
1. **节点链 smoke test**:构造 3 节点链(等 F2 → 按 F2 → 等 ContinueArrow → 按 Space → null),手动跑一遍确认 advance / stay / terminate 三种路径都对。
2. **retry counter**:构造一个永远 miss 的节点(`MaxRetries=3`,`SuccessTemplate` 不存在),确认 3 次 miss 后 runner 终止,running total 是 3。
3. **hit 后 reset**:构造节点,先 miss 2 次,然后 hit,确认 `RetryCount` 归零,下一次 miss 从 1 开始。
4. **复合操作拆分**:daliyUi 节点(命中后内检 DailySel/NoSel)+ DaliyCheck 节点(单帧 DaliyFin/Reach + 分支),跟现有 `TestZzzTaskTrigger.RunSeqDailySel` 行为对齐。

测试 trigger 暂不开 UI 开关,跟现有 `TestZzzTaskTrigger` / `CommonZzzTaskTrigger` 一样由 dispatcher 的 `SetNewTriggerEnabled(bool)` 提供 hot-toggle。Phase 1 不加 ViewModel/UI 绑定,只确保代码路径可用。

## 迁移计划

### Phase 1:基础设施(本 spec)

1. 新建 `IWorkflowNode.cs`
2. 新建 `WorkflowNodeBase.cs`
3. 新建 `TemplateWorkflowNode.cs` / `HsvWorkflowNode.cs` / `OcrWorkflowNode.cs`
4. ~~新建 `WorkflowRunner.cs`~~(中途取消 — 见组件清单备注)
5. 新建 `NewZzzTaskTrigger.cs`(只验证 2 个节点,不复制 21 段;持 `_current` 字段,每帧 `OnCapture` 走一步)
6. 修改 `ZzzTaskTriggerDispatcher.cs` 加 `CreateNewTrigger()` + `SetNewTriggerEnabled(bool)`
7. 跑通 smoke test,确认架构工作

### Phase 2:Test trigger 迁移(后续 spec)

把 `TestZzzTaskTrigger` 的 21 段拆成 IWorkflowNode 链,验证 DailySel 复合节点拆分后的行为跟原版一致。然后删除 `TestZzzTaskTrigger` 的 retreat/scan 状态机。

### Phase 3:Common trigger 评估(后续 spec)

决定 Common trigger 是迁移到 IWorkflowNode(用一种"自循环"节点模式),还是保留现有 delegate 元组模式。

## 风险

- **复合操作拆分后行为差异**:现有 `RunSeqDailySel` 在 daliyUi 命中帧内同时 match DailySel/NoSel,新架构把这个内检测塞进 Operation,逻辑等价但代码位置变了。Phase 1 测试必须对齐行为。
- **retry counter 在节点切换时不重置**:`SuccessTemplate` 是新节点,新节点有自己的 counter,不需要手动重置。但如果 trigger 在节点切换后又回到旧节点(re-entry),旧节点的 counter 还在 — 这可能是想要的(避免新 bug),也可能是 bug 来源。Phase 1 不涉及,但 Phase 2 需要明确语义。
- **Operation 抛异常的语义**:用户决定"不用管",意味着 Operation 必须自己保证不抛。如果 Operation 是用户写的 lambda 且有 bug,会终止工作流。需要日志清晰(节点 Label + 异常类型 + Message)。

## 决策记录

| 决策 | 选项 | 选择 | 理由 |
|------|------|------|------|
| IWorkflowNode vs IZzzWaitTarget | 重命名 / 并存 / 部分迁移 | **并存** | 最小爆炸半径,Common trigger 不动 |
| MatchAndOperation 循环归属 | 自带 / 外部 runner | **外部 runner** | retry 状态在节点,轮询在 runner,职责清晰 |
| 重试语义 | 单次调用内 / 跨调用累计 | **跨调用累计** | 表达"持续 K 次失败才放弃"语义 |
| 异常处理 | 吞掉 / 上浮 / 上浮+不重置 | **上浮** | 用户决定"不用管",错误不隐藏 |
| 命名 | ITemplate / IWorkflowNode / INode | **IWorkflowNode** | 准确表达"工作流节点"语义 |
| 复合操作 | 单节点 / 拆节点 / 特殊路径 | **拆节点** | 状态机更细,Operation 职责单一 |
| 默认实现位置 | 默认接口方法 / 抽象基类 | **抽象基类** | retry counter 是实例状态,基类最自然 |
| 与现有 IZzzWaitTarget 关系 | 组合 / 重写检测 | **组合** | 不重写已有可工作的检测逻辑 |
