using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BetterGenshinImpact.View.Windows;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// Phase 1 验证 trigger:用 K 个节点的 IWorkflowNode 链验证新架构(K 全部来自目录扫描)。
///
/// 节点链 common[0..K-1]:每个 PNG 一个 <see cref="TemplateWorkflowNode"/>,按文件名 Ordinal 排序
/// 收集到 <see cref="_commonNodes"/> 列表。Operation + SuccessTemplate 都由
/// <see cref="ConfigureCommonNodes"/> 内联 switch 按文件名统一赋:daliyF2 →
/// PressKeyForeground(F2) + 推进 list[i+1];daliyNoSel → ClickMatchedArea + 终止(null);
/// 未知 → no-op + warn + 终止。_current = common[0] 作为入口。
///
/// 命中画框由 base class(<see cref="WorkflowNodeBase.MatchAndOperation"/>)统一处理:
/// 建节点时把 <see cref="_overlay"/> 传给 TemplateWorkflowNode,base class 命中时自动调
/// <see cref="ZzzTriggerActions.DrawHitRect"/> 在 overlay 上画框。
///
/// 每帧一次 OnCapture = 工作流走一步:_current.MatchAndOperation(content) → 返回
/// 下一节点 / null / 自身。Dispatcher 50ms timer 自然提供轮询节奏,不再需要内部 sleep。
///
/// 节点配 build/configure 二分:<see cref="BuildCommonTemplateChain"/> 只把 PNG 解析成节点
/// (Operation 用占位 NoOp,SuccessTemplate 留 null);<see cref="ConfigureCommonNodes"/> 遍历
/// 节点按文件名查表统一赋 Operation + SuccessTemplate(Operation 现在是带 internal set 的属性)。
///
/// Phase 2 会用同样的模式把 TestZzzTaskTrigger 的 21 段全部拆成节点链,本 trigger 那时会被删掉。
/// </summary>
public sealed class NewZzzTaskTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "NewZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    private readonly ZzzOverlayWindow? _overlay;
    private IWorkflowNode? _current;

    // common 节点列表:为了 Dispose 时能级联释放 Mat,持有强引用(不只靠 SuccessTemplate 链)
    private readonly List<TemplateWorkflowNode> _commonNodes;

    private const double MatchThreshold = 0.96;
    private const int MaxRetries = 6;

    // 模板列表目录:扫描 GameTask/Zzz/Template/Common 下所有 PNG(由 TemplateImage.EnumerateTemplateFiles 列出)
    private const string CommonTemplateDir = "GameTask\\Zzz\\Template\\Common";

    // Operation 占位(被 ConfigureCommonNodes 立刻覆盖,但 ctor 必须传一个非 null 值)
    private static readonly Action<ZzzCaptureContent, TemplateMatchResult?> NoOpPlaceholder = (_, _) => { };

    public NewZzzTaskTrigger(ZzzOverlayWindow? overlay = null)
    {
        _overlay = overlay;

        // 1) Build:扫目录,把每个 PNG 解析成一个 TemplateWorkflowNode 加进列表
        //    (Operation 用 NoOp 占位,SuccessTemplate 留 null —— 都不在这里配)
        var files = TemplateImage.EnumerateTemplateFiles(CommonTemplateDir);
        _commonNodes = BuildCommonTemplateChain(files);

        // 2) Configure:遍历 nodes 按文件名查表统一赋 Operation + SuccessTemplate
        ConfigureCommonNodes(_commonNodes, files);

        // 3) _current 直接指向 common[0] 作为工作流入口(common 链空时退化为 null)
        _current = _commonNodes.Count > 0 ? _commonNodes[0] : null;
    }

    /// <summary>
    /// 纯 build:扫到的每个 PNG → 一个 <see cref="TemplateWorkflowNode"/>,按文件名字序收集到
    /// <see cref="_commonNodes"/>。Operation 用 <see cref="NoOpPlaceholder"/> 占位,SuccessTemplate
    /// 留 null —— 这两个都由 <see cref="ConfigureCommonNodes"/> 后续填,本方法不做任何 per-name 决策。
    /// </summary>
    private List<TemplateWorkflowNode> BuildCommonTemplateChain(IReadOnlyList<string> files)
    {
        var nodes = new List<TemplateWorkflowNode>(files.Count);
        foreach (var file in files)
        {
            // 文件名约定:<name>_<x>_<y>_<w>x<h>(_<padding>).png,取第一个 _ 前的部分作为模板名
            var name = Path.GetFileNameWithoutExtension(file).Split('_')[0];
            nodes.Add(new TemplateWorkflowNode(
                label: $"NewSeq2/Common/{name}",
                maxRetries: MaxRetries,
                successTemplate: null,
                templatePath: file,
                threshold: MatchThreshold,
                operation: NoOpPlaceholder,
                overlay: _overlay));  // base class 命中时自动画框
        }
        return nodes;
    }

    /// <summary>
    /// 纯 configure:遍历 <paramref name="nodes"/>,对每个节点按文件名 switch 直接赋
    /// Operation + SuccessTemplate。daliyF2 → 推进 list[i+1];daliyNoSel/未知 → 终止(null)。
    ///
    /// 命中画框不在这里处理 —— 节点 ctor 已经把 overlay 传给 base,base class 命中时
    /// 自动调 <see cref="ZzzTriggerActions.DrawHitRect"/>。
    /// </summary>
    private void ConfigureCommonNodes(IReadOnlyList<TemplateWorkflowNode> nodes, IReadOnlyList<string> files)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            // 同上,strip 坐标后缀只留 <name>
            var name = Path.GetFileNameWithoutExtension(files[i]).Split('_')[0];
            IWorkflowNode? next = (i + 1 < nodes.Count) ? nodes[i + 1] : null;

            switch (name)
            {
                // 对齐 CommonZzzTaskTrigger._entries[0] (F2):
                case "daliyF2":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{name} hit → press F2");
                        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F2, content);
                    };
                    node.SuccessTemplate = next;
                    break;

                // 对齐 TestZzzTaskTrigger.RunSeqDailySel (DailyNoSel → click 后终止):
                case "daliyNoSel":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{name} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = null;
                    break;

                default:
                    Debug.WriteLine($"[ZZZ-Workflow-New] common/{name} no-op (未知模板,Operation 未配置)");
                    node.Operation = (content, result) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{name} hit (no-op) score={result?.Score:F3}");
                    };
                    node.SuccessTemplate = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 每帧一次:调当前节点的 MatchAndOperation,根据返回值推进 _current。
    /// dispatcher 50ms timer 提供轮询节奏,不需要内部 sleep。
    /// </summary>
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
        // _current.Dispose() 只会 dispose 当前节点;其他 common 节点也需要释放 Mat。
        // 通过 _commonNodes 字段强引用兜底。
        foreach (var n in _commonNodes)
        {
            if (!ReferenceEquals(n, _current))
            {
                n.Dispose();
            }
        }
    }
}