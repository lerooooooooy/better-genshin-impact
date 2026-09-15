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
/// 链(主路径):daliyF2 → daliyFin → OcrActivityFull → daliyGo → OcrTingmanMaster → ContinueArrow →
/// OcrTingmanSpecial → betterySkip → getBattery → null(链尾)。daliyFin 和 OcrActivityFull
/// 都是"日常已完成"早退检查(各自 SuccessTemplate = null,FailTemplate 接后继节点)。
/// FailTemplate 分支(2 条):
///   - OcrTingmanMaster 重试耗尽 → OcrHouHou → OcrAjiu → rwd1 → rwd2 → rwd4 → rwd5 → rwd6 →
///     daliyF2(闭环回主入口,跳过汀曼对话 + 奖励弹窗序列)。
///   - OcrTingmanSpecial 重试耗尽 → OcrShopStatus → OcrYesterdayBill → shop2 → shop3 → shop4 →
///     shop5 → shop6 → OcrReadyToOpen → OcrHonestBusiness → OcrShengyiXinglong → null(点查看经营
///     状况 → OCR 昨日账本按 ESC → 商铺交互 → 准备营业/诚信经营 OCR → 生意兴隆 OCR 按 ESC兜底)。
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
/// <see cref="ZzzTaskTriggerDispatcher.DrawMatchRect"/> 画框。OCR 点击节点
/// (<see cref="BuildOcrClickOperation"/>)画的是 OCR 命中文字的 bbox(<see cref="OcrWorkflowNode.LastOcrBbox"/>),
/// 命中坐标更准确,便于调试肉眼比对。
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

    // dispatcher Stop 回调:由 dispatcher 在 CreateNewTrigger 时注入
    // (ZzzTaskTriggerDispatcher.CreateNewTrigger 用方法组 Stop 直接传);
    // trigger 在"日常已完成早退"或"扫描触发超限"时调用,真正停掉截图器 + timer。
    // null = 单测 / 测试场景(没有 dispatcher 包住),仅靠 SuccessTemplate=null 软终止。
    private readonly Action? _requestDispatcherStop;

    // 所有节点的 map:label → node。ConfigureCommonNodes 通过 label switch 统一装配
    // Operation(模板节点)+ SuccessTemplate(所有节点)。OCR / HSV 节点不需要字段引用,
    // 直接 ctor 局部变量建好注册即可。
    // 类型用 WorkflowNodeBase 而非 IWorkflowNode —— 后者 SuccessTemplate 只暴露 { get; },
    // 而 WorkflowNodeBase 在同 assembly 内有 internal set,触发器装配链需要写。
    // Dispose 直接遍历 map(模板节点释放 Mat,OCR/HSV Dispose 是 no-op)。
    private readonly Dictionary<string, WorkflowNodeBase> _nodesByLabel;

    // 全节点扫描触发次数:每次 _current == null 进入扫描 +1(命中也计数,不重置);
    // 达到 <see cref="MaxScanTriggers"/> 时停掉 dispatcher(兜底防无限循环 — 链尾终止后
    // _current 重新变 null, 没有这个上限会一直扫描)。
    private int _scanTriggerCount;

    private const double MatchThreshold = 0.96;
    private const int MaxRetries = 10;

    // 扫描触发上限(总次数,不重置):3 次后 stop dispatcher,不再扫描。
    private const int MaxScanTriggers = 3;

    // 模板列表目录:扫描 GameTask/Zzz/Template/Common 下所有 PNG(由 TemplateImage.EnumerateTemplateFiles 列出)
    private const string CommonTemplateDir = "GameTask\\Zzz\\Template\\Common";

    // "汀曼大师" OCR 区域 + 文本(挂在 daliyGo 之后,ContinueArrow 之前)
    private static readonly CvRect TingmanMasterRoi = new(461, 14, 986, 745);
    private const string TingmanMasterText = "汀曼大师";

    // "活跃度已满" OCR 区域 + 文本(挂在 daliyF2 之后,作为"日常已完成"早退检查):
    // OCR 命中 → 点击固定 UI 区域 ActivityFullClickRect(确认/关闭)→ 终止工作流
    // (SuccessTemplate = null);FailTemplate = daliyGo,日常未完成时跳过此检查走正常流程。
    private static readonly CvRect ActivityFullRoi = new(691, 832, 469, 111);
    private static readonly CvRect ActivityFullClickRect = new(1499, 278, 97, 43);
    private const string ActivityFullText = "活跃度已满";

    // "汀曼特调" OCR 区域 + 文本(挂在 ContinueArrow 之后):命中后点击 ROI 本身
    private static readonly CvRect TingmanSpecialRoi = new(1538, 556, 116, 68);
    private const string TingmanSpecialText = "汀曼特调";

    // "吼吼先生" OCR 区域 + 文本(挂在 OcrTingmanMaster 的 FailTemplate):主路径 OCR 汀曼大师
    // 重试耗尽时退化路径,识别成功后按 F。SuccessTemplate 接 ContinueArrow 跳过汀曼对话
    // 直接进 »» 箭头阶段(假设按 F 后 UI 状态对齐)。
    private static readonly CvRect HouHouRoi = new(876, 10, 286, 144);
    private const string HouHouText = "吼吼先生";

    // "阿玖" OCR 区域 + 文本(挂在 OcrHouHou 的 FailTemplate):吼吼先生 OCR 失败时
    // 再退化路径,识别成功后同样按 F;SuccessTemplate 接 rwd1(对齐 OcrHouHou pattern)。
    private static readonly CvRect AjiuRoi = new(786, 242, 203, 196);
    private const string AjiuText = "阿玖";

    // "查看经营状况" OCR 区域 + 文本(挂在 OcrTingmanSpecial 的 FailTemplate):
    // 主路径 OcrTingmanSpecial 重试耗尽时的退化路径(类似"汀曼特调"按钮未识别但 ROI
    // 内出现"查看经营状况"按钮时尝试点击)。命中后点 ROI 内随机位置。
    private static readonly CvRect ShopStatusRoi = new(1297, 481, 620, 358);
    private const string ShopStatusText = "查看经营状况";

    // "昨日账本" OCR 区域 + 文本(挂在 OcrShopStatus 之后):命中后按 ESC 关闭弹窗。
    private static readonly CvRect YesterdayBillRoi = new(525, 210, 154, 65);
    private const string YesterdayBillText = "昨日账本";

    // shop2 命中后点击固定 UI 区域(模板正下方按钮,跟 shop2 模板位置无关):对齐
    // TestZzzTaskTrigger.Shop2ClickRect。
    private static readonly CvRect Shop2ClickRect = new(829, 642, 145, 228);

    // shop3 命中后点击右下角固定 UI 区域(模板在左下,点击在右下):对齐
    // TestZzzTaskTrigger.Shop3ClickRect。
    private static readonly CvRect Shop3ClickRect = new(1605, 1008, 204, 38);

    // "准备营业" OCR 区域 + 文本(挂在 FailTemplate 分支 shop6 之后):
    // 命中后点击固定 UI 区域(跟 OCR 区域无关 — 固定按钮位置)。对齐 TestZzzTaskTrigger.RunSeqReadyToOpen。
    private static readonly CvRect ReadyToOpenRoi = new(912, 498, 123, 52);
    private static readonly CvRect ReadyToOpenClickRect = new(1029, 606, 188, 36);
    private const string ReadyToOpenText = "准备营业";

    // "诚信经营" OCR 区域 + 文本(挂在 FailTemplate 分支 OcrReadyToOpen 之后):
    // 命中后点击固定 UI 区域。模式同 OcrReadyToOpen,完全对齐 TestZzzTaskTrigger.RunSeqHonestBusiness。
    private static readonly CvRect HonestBusinessRoi = new(811, 507, 124, 36);
    private static readonly CvRect HonestBusinessClickRect = new(846, 604, 218, 42);
    private const string HonestBusinessText = "诚信经营";

    // "生意兴隆" OCR 区域 + 文本(挂在 FailTemplate 分支 OcrHonestBusiness 之后):
    // 命中后按 ESC 关闭弹窗(对齐 OcrYesterdayBill 的"按 ESC"模式)。
    private static readonly CvRect ShengyiXinglongRoi = new(770, 451, 350, 132);
    private const string ShengyiXinglongText = "生意兴隆";

    // 领取电池弹窗的"已领取/确认"按钮区域:固定位置,跟 getBattery 模板(907,464 弹窗图)无关。
    // 对齐 TestZzzTaskTrigger.RunSeqGetBattery:模板命中后点击此固定按钮区,
    // 不能用 ClickMatchedArea(会点到弹窗图中央而不是按钮)。
    private static readonly CvRect GetBatteryClickRect = new(854, 709, 203, 37);

    // Operation 占位(被 ConfigureCommonNodes 立刻覆盖,但 ctor 必须传一个非 null 值)
    private static readonly Action<ZzzCaptureContent, TemplateMatchResult?> NoOpPlaceholder = (_, _) => { };

    public NewZzzTaskTrigger(ZzzOverlayWindow? overlay = null, Action? requestDispatcherStop = null)
    {
        _overlay = overlay;
        _requestDispatcherStop = requestDispatcherStop;

        // 节点 map(单一真源):label → node。
        _nodesByLabel = new Dictionary<string, WorkflowNodeBase>();

        // 1) Build:扫目录,把每个 PNG 解析成一个 TemplateWorkflowNode 直接入 map
        //    (Operation 用 NoOp 占位,SuccessTemplate 留 null —— 都不在这里配)
        BuildCommonTemplateChain(TemplateImage.EnumerateTemplateFiles(CommonTemplateDir));

        // OCR 节点同样在 ctor 建好(ROI + targetText 已知);SuccessTemplate 留给 ConfigureCommonNodes 串链
        // 顺序:daliyF2 → OcrActivityFull → (daliyGo via FailTemplate,或 null via SuccessTemplate)。
        // OcrActivityFull 是"日常已完成"早退检查:OCR 命中 → 终止;miss → 走 daliyGo 继续。
        var activityFullNode = new OcrWorkflowNode(
            label: "OcrActivityFull",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 装 SuccessTemplate + FailTemplate
            roi: ActivityFullRoi,
            targetText: ActivityFullText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ActivityFullRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrActivityFull");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrActivityFull hit → click fixed button rect (daily complete, exiting)");
                ZzzTriggerActions.ClickRect(content, ActivityFullClickRect);
            },
            overlay: _overlay);
        _nodesByLabel[activityFullNode.Label] = activityFullNode;

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

        // "汀曼特调" OCR 节点:挂在 ContinueArrow 之后,命中后点击"识别出的文字所在 region"内随机位置。
        // Operation 需要读 self.LastOcrBbox → 先用 NoOp 占位构造,再赋真正的 operation。
        var tingmanSpecialNode = new OcrWorkflowNode(
            label: "OcrTingmanSpecial",
            maxRetries: MaxRetries,
            successTemplate: null,  // SuccessTemplate 由 ConfigureCommonNodes switch 装配(ContinueArrow case 指向 OcrTingmanSpecial)
            roi: TingmanSpecialRoi,
            targetText: TingmanSpecialText,
            operation: NoOpPlaceholder,  // 占位,赋值见下一行
            overlay: _overlay);
        tingmanSpecialNode.Operation = BuildOcrClickOperation(tingmanSpecialNode, "OcrTingmanSpecial");
        _nodesByLabel[tingmanSpecialNode.Label] = tingmanSpecialNode;

        // "吼吼先生" OCR 节点:OcrTingmanMaster 重试耗尽时的退化路径(走 FailTemplate)。
        // 命中后按 F;SuccessTemplate 由 ConfigureCommonNodes 接到 ContinueArrow(跳过汀曼对话
        // 直接进 »» 箭头阶段 —— 假设按 F 后 UI 与正常汀曼路径对齐)。
        // FailTemplate = OcrAjiu(再退化:吼吼先生也失败时识别"阿玖")。
        var houHouNode = new OcrWorkflowNode(
            label: "OcrHouHou",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes wiring 段接 ContinueArrow
            roi: HouHouRoi,
            targetText: HouHouText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(HouHouRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrHouHou");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrHouHou hit → press F (OcrTingmanMaster failure recovery)");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F, content);
            },
            overlay: _overlay);
        _nodesByLabel[houHouNode.Label] = houHouNode;

        // "阿玖" OCR 节点:OcrHouHou 重试耗尽时的再退化路径(走 OcrHouHou 的 FailTemplate)。
        // 命中后按 F;SuccessTemplate 由 ConfigureCommonNodes 接到 rwd1(对齐 OcrHouHou pattern:
        // 都按 F 后接奖励弹窗序列)。FailTemplate 留 null → 自己失败时终止工作流。
        var ajiuNode = new OcrWorkflowNode(
            label: "OcrAjiu",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes wiring 段接 rwd1
            roi: AjiuRoi,
            targetText: AjiuText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(AjiuRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrAjiu");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrAjiu hit → press F (OcrHouHou failure recovery)");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F, content);
            },
            overlay: _overlay);
        _nodesByLabel[ajiuNode.Label] = ajiuNode;

        // "查看经营状况" OCR 节点:OcrTingmanSpecial 重试耗尽时的退化路径
        // (走 OcrTingmanSpecial 的 FailTemplate)。命中后点"识别出的文字所在 region"内随机位置
        // (对齐 OcrTingmanSpecial pattern);SuccessTemplate 由 ConfigureCommonNodes 接 OcrYesterdayBill。
        var shopStatusNode = new OcrWorkflowNode(
            label: "OcrShopStatus",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 接 OcrYesterdayBill
            roi: ShopStatusRoi,
            targetText: ShopStatusText,
            operation: NoOpPlaceholder,  // 占位,赋值见下一行
            overlay: _overlay);
        shopStatusNode.Operation = BuildOcrClickOperation(shopStatusNode, "OcrShopStatus");
        _nodesByLabel[shopStatusNode.Label] = shopStatusNode;

        // "昨日账本" OCR 节点:挂在 OcrShopStatus 之后(走 OcrShopStatus 的 SuccessTemplate)。
        // 命中后按 ESC 关闭弹窗;SuccessTemplate 由 ConfigureCommonNodes 接 shop2(对齐 OcrTingmanMaster 等
        // OCR-命中按 X 模式:Operation 在 ctor 配好,switch 只装 SuccessTemplate)。
        var yesterdayBillNode = new OcrWorkflowNode(
            label: "OcrYesterdayBill",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 装 SuccessTemplate
            roi: YesterdayBillRoi,
            targetText: YesterdayBillText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(YesterdayBillRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrYesterdayBill");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrYesterdayBill hit → press ESC (close bill popup)");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, content);
            },
            overlay: _overlay);
        _nodesByLabel[yesterdayBillNode.Label] = yesterdayBillNode;

        // "准备营业" OCR 节点:挂在 FailTemplate 分支 shop6 之后。
        // 命中后点击固定 UI 区域 ReadyToOpenClickRect(跟 OCR 区域无关)。
        // 对齐 TestZzzTaskTrigger.RunSeqReadyToOpen(画 ROI + 点固定按钮)。
        var readyToOpenNode = new OcrWorkflowNode(
            label: "OcrReadyToOpen",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 装 SuccessTemplate
            roi: ReadyToOpenRoi,
            targetText: ReadyToOpenText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ReadyToOpenRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrReadyToOpen");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrReadyToOpen hit → click fixed button rect");
                ZzzTriggerActions.ClickRect(content, ReadyToOpenClickRect);
            },
            overlay: _overlay);
        _nodesByLabel[readyToOpenNode.Label] = readyToOpenNode;

        // "诚信经营" OCR 节点:挂在 FailTemplate 分支 OcrReadyToOpen 之后。
        // 命中后点击固定 UI 区域 HonestBusinessClickRect。模式同 OcrReadyToOpen。
        // 对齐 TestZzzTaskTrigger.RunSeqHonestBusiness。
        var honestBusinessNode = new OcrWorkflowNode(
            label: "OcrHonestBusiness",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 装 SuccessTemplate
            roi: HonestBusinessRoi,
            targetText: HonestBusinessText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(HonestBusinessRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrHonestBusiness");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrHonestBusiness hit → click fixed button rect");
                ZzzTriggerActions.ClickRect(content, HonestBusinessClickRect);
            },
            overlay: _overlay);
        _nodesByLabel[honestBusinessNode.Label] = honestBusinessNode;

        // "生意兴隆" OCR 节点:挂在 FailTemplate 分支 OcrHonestBusiness 之后(走 OcrHonestBusiness
        // 的 SuccessTemplate)。命中后按 ESC 关闭弹窗(对齐 OcrYesterdayBill 的"按 ESC"模式);
        // SuccessTemplate 留 null → 自己失败终止。
        var shengyiXinglongNode = new OcrWorkflowNode(
            label: "OcrShengyiXinglong",
            maxRetries: MaxRetries,
            successTemplate: null,  // 由 ConfigureCommonNodes switch 显式留 null
            roi: ShengyiXinglongRoi,
            targetText: ShengyiXinglongText,
            operation: (content, _) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ShengyiXinglongRoi, content.Image.Width, content.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    content,
                    "OcrShengyiXinglong");
                Debug.WriteLine($"[ZZZ-Workflow-New] common/ocrShengyiXinglong hit → press ESC (close flourish popup)");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, content);
            },
            overlay: _overlay);
        _nodesByLabel[shengyiXinglongNode.Label] = shengyiXinglongNode;

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
    /// (lambda 引用 ROI/Text/detector 常量),switch 跳过 Operation,只装 SuccessTemplate /
    /// FailTemplate(后者用于重试耗尽时的退化路径)。
    ///
    /// 主链:daliyF2 → daliyFin → OcrActivityFull → daliyGo → OcrTingmanMaster → ContinueArrow →
    /// OcrTingmanSpecial → betterySkip → getBattery → null(链尾);daliyFin 和 OcrActivityFull 是
    /// 两道"日常已完成"早退检查(各自 SuccessTemplate = null,FailTemplate 接后继)。
    /// FailTemplate 分支:
///   - OcrTingmanMaster → OcrHouHou → OcrAjiu → rwd1 → rwd2 → rwd4 → rwd5 → rwd6 → daliyF2(闭环)。
///   - OcrTingmanSpecial → OcrShopStatus → OcrYesterdayBill → shop2 → shop3 → shop4 → shop5 →
///     shop6 → OcrReadyToOpen → OcrHonestBusiness → OcrShengyiXinglong → null。
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
                    // daliyF2 按 F2 后接 daliyFin 早退检查;daliyFin 的 FailTemplate 接 OcrActivityFull,
                    // OcrActivityFull 的 FailTemplate 再接回 daliyGo(未完成时正常推进)
                    node.SuccessTemplate = nodesByLabel["daliyFin"];
                    break;

                // daliyFin 挂在 daliyF2 之后:日常已完成(绿色对勾)早退检查。
                // 模板命中 → 主动调 dispatcher Stop(截图器 + timer 真正停掉,而不是仅软终止
                // 下一帧又扫回主入口);FailTemplate = OcrActivityFull(未完成时跳过此检查,
                // 继续下一道早退)。
                case "daliyFin":
                    node.Operation = (content, result) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit (daily complete, stop dispatcher)");
                        _requestDispatcherStop?.Invoke();
                    };
                    node.SuccessTemplate = null;
                    node.FailTemplate = nodesByLabel["OcrActivityFull"];
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

                // rwd1 / rwd2 挂在 FailTemplate 分支 OcrHouHou 之后:
                // 对齐 TestZzzTaskTrigger.RunSeqRwd1 / RunSeqRin 模式(都是 ClickMatchedArea)。
                // rwd2 文件对应 TestZzzTaskTrigger 的 Rin 节点(seq9)。
                case "rwd1":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["rwd2"];
                    break;

                case "rwd2":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["rwd4"];
                    break;

                // rwd4 / rwd5 / rwd6 挂在 FailTemplate 分支 rwd2 之后:
                // 对齐 TestZzzTaskTrigger.RunSeqRwd4 / RunSeqRwd5 / RunSeqRwd6 模式
                // (seq 11 / 12 / 6 都是命中后按 ESC 关闭弹窗/退出当前状态)。
                case "rwd4":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → press ESC");
                        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, content);
                    };
                    node.SuccessTemplate = nodesByLabel["rwd5"];
                    break;

                case "rwd5":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → press ESC");
                        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, content);
                    };
                    node.SuccessTemplate = nodesByLabel["rwd6"];
                    break;

                case "rwd6":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → press ESC");
                        ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, content);
                    };
                    // rwd6 ESC 后回到 daliyF2 主入口:走完整主路径再次尝试;对齐"失败恢复分支闭环"
                    node.SuccessTemplate = nodesByLabel["daliyF2"];
                    break;

                // 特殊节点:Operation 在 ctor 配好,switch 只装 SuccessTemplate / FailTemplate
                case "OcrTingmanMaster":
                    node.SuccessTemplate = nodesByLabel["ContinueArrow"];
                    // 失败路径:OCR 汀曼大师重试耗尽 → 退化到吼吼先生 OCR(按 F 跳过汀曼对话)
                    node.FailTemplate = nodesByLabel["OcrHouHou"];
                    break;

                case "OcrActivityFull":
                    // 日常已完成早退:OCR 命中 → 点击确认按钮 → 终止工作流;
                    // FailTemplate = daliyGo(日常未完成时跳过此检查,正常推进)
                    node.SuccessTemplate = null;
                    node.FailTemplate = nodesByLabel["daliyGo"];
                    break;

                case "OcrHouHou":
                    // 退化路径:吼吼先生 OCR 命中后按 F,SuccessTemplate 接 rwd1(继续走奖励弹窗序列);
                    // FailTemplate = OcrAjiu(再退化:吼吼先生也失败时识别"阿玖")
                    node.SuccessTemplate = nodesByLabel["rwd1"];
                    node.FailTemplate = nodesByLabel["OcrAjiu"];
                    break;

                case "OcrAjiu":
                    // 再退化路径:阿玖 OCR 命中后按 F,SuccessTemplate 接 rwd1(对齐 OcrHouHou pattern);
                    // FailTemplate 留 null → 自己失败终止
                    node.SuccessTemplate = nodesByLabel["rwd1"];
                    break;

                case "ContinueArrow":
                    node.SuccessTemplate = nodesByLabel["OcrTingmanSpecial"];
                    break;

                case "OcrTingmanSpecial":
                    node.SuccessTemplate = nodesByLabel["betterySkip"];
                    // 失败路径:OCR 汀曼特调重试耗尽 → 退化到查看经营状况 OCR(点 OCR 命中文字 bbox)
                    node.FailTemplate = nodesByLabel["OcrShopStatus"];
                    break;

                case "OcrShopStatus":
                    // 退化路径:查看经营状况 OCR 命中后点 OCR 命中文字 bbox;SuccessTemplate 接 OcrYesterdayBill(按 ESC 关弹窗)
                    node.SuccessTemplate = nodesByLabel["OcrYesterdayBill"];
                    break;

                case "OcrYesterdayBill":
                    // 退化路径续:昨日账本 OCR 命中后按 ESC 关弹窗;SuccessTemplate 接 shop2(进入商铺交互序列)
                    node.SuccessTemplate = nodesByLabel["shop2"];
                    break;

                // shop2 / shop3 挂在 FailTemplate 分支 OcrYesterdayBill 之后:
                // 对齐 TestZzzTaskTrigger.RunSeqShop2 / RunSeqShop3 模式(命中后点击固定 UI 区域,
                // 不是模板匹配框 — 模板位置 ≠ 按钮位置)。
                case "shop2":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click fixed button rect");
                        ZzzTriggerActions.ClickRect(content, Shop2ClickRect);
                    };
                    node.SuccessTemplate = nodesByLabel["shop3"];
                    break;

                case "shop3":
                    node.Operation = (content, _) =>
                    {
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click fixed button rect");
                        ZzzTriggerActions.ClickRect(content, Shop3ClickRect);
                    };
                    node.SuccessTemplate = nodesByLabel["shop4"];
                    break;

                // shop4 / shop5 / shop6 挂在 FailTemplate 分支 shop3 之后:
                // 对齐 TestZzzTaskTrigger.RunSeqShop4 / RunSeqShop5 / RunSeqShop6 模式
                // (命中后点击模板匹配框内随机位置 — 模板本身就是要点的按钮)。
                case "shop4":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["shop5"];
                    break;

                case "shop5":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    node.SuccessTemplate = nodesByLabel["shop6"];
                    break;

                case "shop6":
                    node.Operation = (content, result) =>
                    {
                        if (result == null) return;
                        Debug.WriteLine($"[ZZZ-Workflow-New] common/{label} hit → click");
                        ZzzTriggerActions.ClickMatchedArea(result, content);
                    };
                    // shop6 点击后接 OcrReadyToOpen(商铺交互完成后进入"准备营业"OCR 检测)
                    node.SuccessTemplate = nodesByLabel["OcrReadyToOpen"];
                    break;

                // OcrReadyToOpen / OcrHonestBusiness 挂在 FailTemplate 分支 shop6 之后:
                // 对齐 TestZzzTaskTrigger.RunSeqReadyToOpen / RunSeqHonestBusiness 模式
                // (画 ROI + 点击固定 UI 区域)。
                case "OcrReadyToOpen":
                    node.SuccessTemplate = nodesByLabel["OcrHonestBusiness"];
                    break;

                case "OcrHonestBusiness":
                    node.SuccessTemplate = nodesByLabel["OcrShengyiXinglong"];
                    break;

                case "OcrShengyiXinglong":
                    // 商铺交互序列续:生意兴隆 OCR 命中后按 ESC 关弹窗;SuccessTemplate 留 null → 自己失败终止
                    node.SuccessTemplate = null;
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
    /// OCR 点击 operation 工厂:画框 + 点 OCR 命中文字所在 region 内随机位置。
    /// clickRect = <paramref name="node"/>.<see cref="OcrWorkflowNode.LastOcrBbox"/> ?? safeRoi
    /// (bbox 在 capture 坐标系;fallback 到 safeRoi 是因为 OCR bbox 解析异常时仍能工作)。
    /// 同时更新 overlay 上的画框 → OCR bbox(命中时)/ ROI(回退时)而非 ROI,方便观察。
    /// </summary>
    private Action<ZzzCaptureContent, TemplateMatchResult?> BuildOcrClickOperation(OcrWorkflowNode node, string label)
    {
        return (content, _) =>
        {
            var safeRoi = ZzzImageUtils.ClampRoi(node.Roi, content.Image.Width, content.Image.Height);
            var clickRect = node.LastOcrBbox ?? safeRoi;
            ZzzTaskTriggerDispatcher.DrawMatchRect(
                _overlay,
                new System.Drawing.Rectangle(clickRect.X, clickRect.Y, clickRect.Width, clickRect.Height),
                content,
                label);
            Debug.WriteLine($"[ZZZ-Workflow-New] common/{label.ToLowerInvariant()} hit → click random in OCR bbox (fallback ROI)");
            ZzzTriggerActions.ClickRect(content, clickRect);
        };
    }

    /// <summary>
    /// 每帧一次:
    /// 1) 扫描触发已达上限(<see cref="_scanTriggerCount"/> >= <see cref="MaxScanTriggers"/>)→
    ///    已停 dispatcher,直接返回
    /// 2) _current 为 null → 全节点扫描找第一个匹配;触发次数 +1,达到 <see cref="MaxScanTriggers"/>
    ///    时停 dispatcher(兜底,不论本次扫描命中与否)
    /// 3) _current 非 null → 调 MatchAndOperation 推进 _current
    /// dispatcher 50ms timer 提供轮询节奏,不需要内部 sleep。
    /// </summary>
    public void OnCapture(ZzzCaptureContent content)
    {
        if (_scanTriggerCount >= MaxScanTriggers)
        {
            // 已达上限且已请求停 dispatcher,等待 dispatcher 完全停止
            return;
        }

        var current = _current;
        if (current == null)
        {
            // 每次进入扫描都 +1(命中也计,不重置),作为"无限循环"安全阀
            _scanTriggerCount++;
            if (_scanTriggerCount >= MaxScanTriggers)
            {
                Debug.WriteLine($"[ZZZ-Workflow-New] scan triggered {_scanTriggerCount}/{MaxScanTriggers} times → stop dispatcher");
                _requestDispatcherStop?.Invoke();
                return;
            }

            current = FindFirstMatchingNode(content);
            _current = current;
            if (current == null)
            {
                Debug.WriteLine($"[ZZZ-Workflow-New] scan miss ({_scanTriggerCount}/{MaxScanTriggers})");
                return;
            }
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