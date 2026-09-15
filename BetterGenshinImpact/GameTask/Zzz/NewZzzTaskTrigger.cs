using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// Phase 1 验证 trigger:用 K 个节点的 IWorkflowNode 链验证新架构(K 全部来自目录扫描)。
///
/// 节点全部注册到 <see cref="_nodesByLabel"/>(label → node),<see cref="ConfigureCommonNodes"/> 通过
/// label switch 统一装配 Operation + SuccessTemplate:模板节点两个都在 switch 内设;OCR/HSV 特殊节点
/// Operation 在 ctor 配好(lambda 引用 ROI/Text/detector 常量),switch 只装 SuccessTemplate。
/// 每个 PNG 一个 <see cref="TemplateWorkflowNode"/> 直接入 map;特殊节点在 ctor 构造并注册到 map。
/// 链:daliyF2 → daliyGo → OcrTingmanMaster → ContinueArrow → OcrTingmanSpecial →
/// betterySkip → getBattery → null(链尾)。
/// 加新特殊节点:ctor 构造并注册 + switch 加 case 设 SuccessTemplate(把它串到前驱上)。
///
/// <para>
/// _current 生命周期:默认 null(不在 ctor 硬编码 entry)。OnCapture 入口检查 _current:为 null
/// 则全节点扫描一次找第一个匹配(<see cref="FindFirstMatchingNode"/>)—— 链尾终止 /
/// 重试耗尽 / 初始态下帧从屏幕状态自动恢复;连续 <see cref="MaxConsecutiveScanMisses"/>
/// 次扫描失败后硬终止(不再扫描,等 trigger 重启);截图器停止时 <see cref="Dispose"/>
/// 显式置 null,不再有 OnCapture 调用所以永远保持 null。
/// </para>
///
/// 命中画框由 base class(<see cref="WorkflowNodeBase.MatchAndOperation"/>)统一处理:
/// 建节点时把 <see cref="_overlay"/> 传给 TemplateWorkflowNode,base class 命中时自动调
/// <see cref="ZzzTriggerActions.DrawHitRect"/> 在 overlay 上画框。HSV / OCR 命中时
/// TemplateMatchResult 为 null,base class 不画 —— 这两类节点(operation lambda 内)需手动调
/// <see cref="ZzzTaskTriggerDispatcher.DrawMatchRect"/> 画 ROI 框。
///
/// 每帧一次 OnCapture = 工作流走一步:_current.MatchAndOperation(content) → 返回
/// 下一节点 / null / 自身。Dispatcher 50ms timer 自然提供轮询节奏,不再需要内部 sleep。
///
/// 节点配 build/configure 二分:<see cref="BuildCommonTemplateChain"/> 只把 PNG 解析成节点
/// 直接入 map(Operation 用占位 NoOp,SuccessTemplate 留 null);<see cref="ConfigureCommonNodes"/>
/// 遍历 map 统一赋 Operation + SuccessTemplate(Operation 是带 internal set 的属性)。
///
/// Phase 2 会用同样的模式把 TestZzzTaskTrigger 的 21 段全部拆成节点链,本 trigger 那时会被删掉。
/// </summary>
public sealed class NewZzzTaskTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "NewZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    private readonly ZzzOverlayWindow? _overlay;
    private IWorkflowNode? _current;

    // 所有节点的 map:label → node。ConfigureCommonNodes 通过 label switch 统一装配
    // Operation(模板节点)+ SuccessTemplate(所有节点)。OCR / HSV 节点不需要字段引用,
    // 直接 ctor 局部变量建好注册即可。
    // 类型用 WorkflowNodeBase 而非 IWorkflowNode —— 后者 SuccessTemplate 只暴露 { get; },
    // 而 WorkflowNodeBase 在同 assembly 内有 internal set,触发器装配链需要写。
    // Dispose 直接遍历 map(模板节点释放 Mat,OCR/HSV Dispose 是 no-op)。
    private readonly Dictionary<string, WorkflowNodeBase> _nodesByLabel;

    // 连续扫描失败计数:每次扫描未命中 +1,扫描命中重置为 0;
    // 达到 <see cref="MaxConsecutiveScanMisses"/> 时 OnCapture 早返回(硬终止)
    private int _consecutiveScanMisses;

    private const double MatchThreshold = 0.96;
    private const int MaxRetries = 10;

    // 连续扫描失败上限:OnCapture 入口 _current null 时全节点扫描,
    // 连续 N 次都找不到匹配就硬终止(不再扫描,等 trigger 重启)。命中即重置计数。
    private const int MaxConsecutiveScanMisses = 3;

    // 模板列表目录:扫描 GameTask/Zzz/Template/Common 下所有 PNG(由 TemplateImage.EnumerateTemplateFiles 列出)
    private const string CommonTemplateDir = "GameTask\\Zzz\\Template\\Common";

    // "汀曼大师" OCR 区域 + 文本(挂在 daliyGo 之后,ContinueArrow 之前)
    private static readonly CvRect TingmanMasterRoi = new(461, 14, 986, 745);
    private const string TingmanMasterText = "汀曼大师";

    // "汀曼特调" OCR 区域 + 文本(挂在 ContinueArrow 之后):命中后点击 ROI 本身
    private static readonly CvRect TingmanSpecialRoi = new(1538, 556, 116, 68);
    private const string TingmanSpecialText = "汀曼特调";

    // 领取电池弹窗的"已领取/确认"按钮区域:固定位置,跟 getBattery 模板(907,464 弹窗图)无关。
    // 对齐 TestZzzTaskTrigger.RunSeqGetBattery:模板命中后点击此固定按钮区,
    // 不能用 ClickMatchedArea(会点到弹窗图中央而不是按钮)。
    private static readonly CvRect GetBatteryClickRect = new(854, 709, 203, 37);

    // Operation 占位(被 ConfigureCommonNodes 立刻覆盖,但 ctor 必须传一个非 null 值)
    private static readonly Action<ZzzCaptureContent, TemplateMatchResult?> NoOpPlaceholder = (_, _) => { };

    public NewZzzTaskTrigger(ZzzOverlayWindow? overlay = null)
    {
        _overlay = overlay;

        // 节点 map(单一真源):label → node。
        _nodesByLabel = new Dictionary<string, WorkflowNodeBase>();

        // 1) Build:扫目录,把每个 PNG 解析成一个 TemplateWorkflowNode 直接入 map
        //    (Operation 用 NoOp 占位,SuccessTemplate 留 null —— 都不在这里配)
        BuildCommonTemplateChain(TemplateImage.EnumerateTemplateFiles(CommonTemplateDir));

        // OCR 节点同样在 ctor 建好(ROI + targetText 已知);SuccessTemplate 留给 ConfigureCommonNodes 串链
        var tingmanMasterNode = new OcrWorkflowNode(
            label: "OcrTingmanMaster",
            maxRetries: MaxRetries,
            successTemplate: null,  // SuccessTemplate 由 ConfigureCommonNodes switch 装配(OcrTingmanMaster case 指向 ContinueArrow)
            roi: TingmanMasterRoi,
            targetText: TingmanMasterText,
            operation: (content, _) =>
            {
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrTingmanMaster hit → press F");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F, content);
            },
            overlay: _overlay);
        _nodesByLabel[tingmanMasterNode.Label] = tingmanMasterNode;

        // 继续对话箭头节点:HSV 检测 + 7 道防线 + 按空格,封装在
        // <see cref="ContinueArrowWorkflowNode"/>。新 trigger 只需 new + 注册 map。
        var continueArrowNode = new ContinueArrowWorkflowNode(MaxRetries, _overlay);
        _nodesByLabel[continueArrowNode.Label] = continueArrowNode;

        // "汀曼特调" OCR 节点:挂在 ContinueArrow 之后,命中后点击 ROI 本身。
        // 与 OcrTingmanMaster 同模式(OcrWorkflowNode sealed → 直接内联构造)。
        var tingmanSpecialNode = new OcrWorkflowNode(
            label: "OcrTingmanSpecial",
            maxRetries: MaxRetries,
            successTemplate: null,  // SuccessTemplate 由 ConfigureCommonNodes switch 装配(ContinueArrow case 指向 OcrTingmanSpecial)
            roi: TingmanSpecialRoi,
            targetText: TingmanSpecialText,
            operation: (content, _) =>
            {
                // OCR 命中时 TemplateMatchResult 为 null,base class 不画 —— 手动画 ROI
                var safeRoi = ZzzImageUtils.ClampRoi(TingmanSpecialRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrTingmanSpecial");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrTingmanSpecial hit → click ROI");
                ZzzTriggerActions.ClickRect(content, TingmanSpecialRoi);
            },
            overlay: _overlay);
        _nodesByLabel[tingmanSpecialNode.Label] = tingmanSpecialNode;

        // 2) Configure:遍历 map 统一赋 Operation + SuccessTemplate(链装配硬编码 label 目标)
        ConfigureCommonNodes(_nodesByLabel);

        // _current 不在这里设,保持 null —— 首帧 / 链尾终止 / 重试耗尽时由
        // <see cref="FindStartNode"/> 重置到 daliyF2 入口
    }

    /// <summary>
    /// 纯 build:扫到的每个 PNG → 一个 <see cref="TemplateWorkflowNode"/>,按文件名字
    /// strip 后的 &lt;name&gt; 作为 label 直接入 <see cref="_nodesByLabel"/>。Operation 用
    /// <see cref="NoOpPlaceholder"/> 占位,SuccessTemplate 留 null —— 这两个都由
    /// <see cref="ConfigureCommonNodes"/> 后续填,本方法不做任何 per-name 决策。
    /// </summary>
    private void BuildCommonTemplateChain(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            // 文件名约定:<name>_<x>_<y>_<w>x<h>(_<padding>).png,取第一个 _ 前的部分作为模板名/label
            var name = Path.GetFileNameWithoutExtension(file).Split('_')[0];
            _nodesByLabel[name] = new TemplateWorkflowNode(
                label: name,
                maxRetries: MaxRetries,
                successTemplate: null,
                templatePath: file,
                threshold: MatchThreshold,
                operation: NoOpPlaceholder,
                overlay: _overlay);  // base class 命中时自动画框
        }
    }

    /// <summary>
    /// 纯 configure:遍历 <paramref name="nodesByLabel"/>,按 label switch 统一装配 Operation
    /// (模板节点)+ SuccessTemplate(所有节点)。OCR/HSV 特殊节点的 Operation 在 ctor 配好
    /// (lambda 引用 ROI/Text/detector 常量),switch 跳过 Operation,只装 SuccessTemplate。
    ///
    /// 已知链:daliyF2 → daliyGo → OcrTingmanMaster → ContinueArrow → OcrTingmanSpecial →
    /// betterySkip → getBattery → null(链尾)。
    /// 未知 label(模板 / 特殊):default 走 no-op log + SuccessTemplate = null(不连任何节点)。
    ///
    /// 命中画框不在这里处理 —— 节点 ctor 已经把 overlay 传给 base,base class 命中时
    /// 自动调 <see cref="ZzzTriggerActions.DrawHitRect"/>。
    /// </summary>
    private void ConfigureCommonNodes(IReadOnlyDictionary<string, WorkflowNodeBase> nodesByLabel)
    {
        foreach (var (label, node) in nodesByLabel)
        {
            switch (label)
            {
                // 模板节点:switch 设 Operation + SuccessTemplate
                // 对齐 CommonZzzTaskTrigger._entries[0] (F2):
                case "daliyF2":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → press F2");
                        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F2, content);
                    };
                    node.SuccessTemplate = nodesByLabel["daliyGo"];
                    break;

                // 对齐 TestZzzTaskTrigger.RunSeqDailySel (DailyGo → click 后插入 OCR 汀曼大师):
                case "daliyGo":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["OcrTingmanMaster"];
                    break;

                // 通用"识别 → 点击识别区域"模板(betterySkip / getBattery 共用模式)。
                // 挂在 OcrTingmanSpecial 之后:对话结束后跳过 betterySkip 提示,再点 getBattery 领电池。
                case "betterySkip":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["getBattery"];
                    break;

                case "getBattery":
                    // 对齐 TestZzzTaskTrigger.RunSeqGetBattery:模板(907,464 弹窗图)
                    // 命中后点击固定"已领取"按钮区(854,709,203x37),不是模板匹配框。
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click fixed button rect");
                        ZzzTriggerActions.ClickRect(content, GetBatteryClickRect);
                    };
                    node.SuccessTemplate = null;  // 链尾:终止
                    break;

                // 特殊节点:Operation 在 ctor 配好,switch 只装 SuccessTemplate
                case "OcrTingmanMaster":
                    node.SuccessTemplate = nodesByLabel["ContinueArrow"];
                    break;

                case "ContinueArrow":
                    node.SuccessTemplate = nodesByLabel["OcrTingmanSpecial"];
                    break;

                case "OcrTingmanSpecial":
                    node.SuccessTemplate = nodesByLabel["betterySkip"];
                    break;

                default:
                    Debug.WriteLine($"[ZZZ-Workflow-New] unknown/{label} no-op (未装配的节点,默认链尾)");
                    // 模板节点的未知 label:Operation 设 no-op;特殊节点 Operation ctor 已配,不动
                    if (node is TemplateWorkflowNode)
                    {
                        node.Operation = (content, result) =>
                        {
                            Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit (no-op) score={result?.Score:F3}");
                        };
                    }
                    node.SuccessTemplate = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 全节点扫描找第一个匹配的:_current 为 null 时由 <see cref="OnCapture"/> 调用,
    /// 实现"链尾终止 / 重试耗尽 / 初始态 → 下帧从屏幕状态自动恢复"的语义。
    /// 每个节点只调一次 <see cref="IWorkflowNode.TryMatch"/>(不内部重试),
    /// 按字典迭代顺序返回第一个 hit;无匹配返回 null。
    /// 字典迭代顺序 = ctor 插入顺序(BuildCommonTemplateChain 按目录扫描顺序入,
    /// 然后 OcrTingmanMaster / ContinueArrow / OcrTingmanSpecial 按 ctor 顺序追加)。
    /// </summary>
    private WorkflowNodeBase? FindFirstMatchingNode(ZzzCaptureContent content)
    {
        foreach (var (label, node) in _nodesByLabel)
        {
            if (node.TryMatch(content, out _))
            {
                return node;
            }
        }
        return null;
    }

    /// <summary>
    /// 每帧一次:
    /// 1) 已硬终止(<see cref="_consecutiveScanMisses"/> >= 上限)→ 直接返回
    /// 2) _current 为 null → 全节点扫描找第一个匹配;连续失败 <see cref="MaxConsecutiveScanMisses"/>
    ///    次后硬终止(后续帧不再扫描,等 trigger 重启)
    /// 3) _current 非 null → 调 MatchAndOperation 推进 _current
    /// dispatcher 50ms timer 提供轮询节奏,不需要内部 sleep。
    /// </summary>
    public void OnCapture(ZzzCaptureContent content)
    {
        if (_consecutiveScanMisses >= MaxConsecutiveScanMisses)
        {
            // 已硬终止,等待 trigger 重启
            return;
        }

        var current = _current;
        if (current == null)
        {
            current = FindFirstMatchingNode(content);
            _current = current;
            if (current == null)
            {
                _consecutiveScanMisses++;
                if (_consecutiveScanMisses >= MaxConsecutiveScanMisses)
                {
                    Debug.WriteLine($"[ZZZ-Workflow-New] scan failed {MaxConsecutiveScanMisses} times consecutively → terminate (waiting for trigger restart)");
                }
                else
                {
                    Debug.WriteLine($"[ZZZ-Workflow-New] scan miss {_consecutiveScanMisses}/{MaxConsecutiveScanMisses}");
                }
                return;
            }
            _consecutiveScanMisses = 0;
            Debug.WriteLine($"[ZZZ-Workflow-New] scan → matched {current.Label}");
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
            Debug.WriteLine($"[ZZZ-Workflow-New] node={current.Label} → terminate (next frame resets to entry)");
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
        // 截图器停止时显式置 null:虽然没有更多 OnCapture 调用,但 _current 持有节点引用,
        // 置 null 避免悬挂引用,GC 也能更快回收 map 里的 Mat 资源
        _current = null;

        // 遍历 map Dispose 所有节点:TemplateWorkflowNode 释放 Mat,OCR / HSV Dispose 是 no-op。
        // 不需要单独处理 _current —— map 里有所有节点的强引用。
        foreach (var node in _nodesByLabel.Values)
        {
            node.Dispose();
        }
    }
}