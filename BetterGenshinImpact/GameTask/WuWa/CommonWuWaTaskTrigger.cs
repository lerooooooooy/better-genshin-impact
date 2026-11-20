using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮通用 trigger:把每个识别目标封装成一个工作流节点,每帧驱动一次。
///
/// 结构对齐 <c>NewZzzTaskTrigger</c>:节点 map(label → node)+ 链装配 + 每帧推进一格。
///
/// 节点清单:
/// - <see cref="WuWaSkipButtonWorkflowNode"/>(SkipButton):画面左上角六边形「跳过」按钮
///   → 画框 + **后台**投递 ESC。检测见 <see cref="WuWaImageUtils.DetectSkipButton"/>(形态学顶帽 + 多边形几何,
///   不用模板图 —— 按钮内部图标大面积透明,模板匹配得分上不去)。
/// - <see cref="WuWaSkipConfirmWorkflowNode"/>(SkipConfirmNotice):「本次登录不再提示」勾选文本
///   → 点击勾选,SuccessTemplate 接 SkipConfirmOk。
/// - <see cref="WuWaSkipConfirmWorkflowNode"/>(SkipConfirmOk):「确认」按钮 → 点击关掉弹窗。
/// - <see cref="WuWaSkipStoryWorkflowNode"/>(SkipStory):右下角「跳过剧情」文字按钮 → 后台点击。
///
/// 每帧流程:
/// 1) 有工作流链在跑(「提示」弹窗:勾选 → 确认)→ <see cref="IWorkflowNode.MatchAndOperation"/> 推进一格;
///    链还在推进时本帧不再扫描(弹窗是模态的,挡住其他目标)。
/// 2) 没有链在跑 → 按 <see cref="_scanOrder"/> 的优先级扫描所有节点,第一个命中的节点被激活。
/// 3) 一个都没命中 → 把 overlay 上的框擦掉(否则会一直留在屏幕上)。
///
/// 后台投递走 PostMessage(WM_KEYDOWN / WM_KEYUP / WM_LBUTTONDOWN),不抢焦点、不切前台。
/// 鸣潮是否消费后台鼠标消息待用户实测(UE 不吃后台鼠标消息,只吃键盘)。
/// </summary>
public sealed class CommonWuWaTaskTrigger : IWuWaTaskTrigger, IDisposable
{
    public string Name => "WuWa Common Trigger";

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 节点连续 miss 的上限(帧)。60ms 一帧 → 30 帧 ≈ 1.5s,
    /// 够盖住「勾选后等 600ms 再点确认」这段延迟。
    /// </summary>
    private const int MaxRetries = 30;

    private readonly ILogger _logger;

    private readonly TriggerOverlayWindow? _overlay;

    /// <summary>
    /// 节点 map:label → node(单一真源)。类型用 <see cref="WorkflowNodeBase"/> 而非
    /// <see cref="IWorkflowNode"/> —— 后者 SuccessTemplate 只暴露 { get; },
    /// 而 WorkflowNodeBase 在同 assembly 内有 internal set,触发器装配链需要写。
    /// </summary>
    private readonly Dictionary<string, WorkflowNodeBase> _nodesByLabel = new();

    /// <summary>
    /// 每帧扫描的优先级(从前往后找第一个命中的节点)。
    /// 字典迭代顺序虽然也是插入顺序,但显式列一份更清楚,也方便以后插新节点。
    /// </summary>
    private readonly List<WorkflowNodeBase> _scanOrder = new();

    /// <summary>
    /// 当前工作流节点(链上的位置);null = 本帧走全节点扫描。
    /// 「提示」弹窗的链:SkipConfirmNotice → SkipConfirmOk → null(结束)。
    /// </summary>
    private IWorkflowNode? _current;

    /// <summary>「提示」弹窗的两个节点共用一个 detector(模板 Mat 不重复加载)。</summary>
    private readonly WuWaSkipConfirmDetector _skipConfirmDetector = new();

    /// <summary>「提示」弹窗的跨节点上下文(勾选只点一次 + 勾选后延迟点确认 + 确认点击冷却 + 逐帧检测缓存)。</summary>
    private readonly WuWaSkipConfirmContext _skipConfirmContext = new();

    /// <summary>
    /// 当前是否有一个画在 overlay 上的命中框。目标消失后要把框擦掉,否则会一直留在屏幕上。
    /// </summary>
    private bool _rectDrawn;

    public CommonWuWaTaskTrigger(TriggerOverlayWindow? overlay = null, ILogger? logger = null)
    {
        _overlay = overlay;
        _logger = logger ?? App.GetLogger<CommonWuWaTaskTrigger>();

        var skipButtonNode = new WuWaSkipButtonWorkflowNode(MaxRetries, _logger, _overlay);
        var skipConfirmNoticeNode = new WuWaSkipConfirmWorkflowNode(
            WuWaSkipConfirmRole.Notice, MaxRetries, _skipConfirmDetector, _skipConfirmContext, _logger, _overlay);
        var skipConfirmOkNode = new WuWaSkipConfirmWorkflowNode(
            WuWaSkipConfirmRole.Ok, MaxRetries, _skipConfirmDetector, _skipConfirmContext, _logger, _overlay);
        var skipStoryNode = new WuWaSkipStoryWorkflowNode(MaxRetries, _logger, _overlay);

        foreach (var node in new WorkflowNodeBase[] { skipButtonNode, skipConfirmNoticeNode, skipConfirmOkNode, skipStoryNode })
        {
            _nodesByLabel[node.Label] = node;
        }

        // 扫描优先级:跳过按钮 → 弹窗(勾选 → 确认)→ 跳过剧情。
        _scanOrder.Add(skipButtonNode);
        _scanOrder.Add(skipConfirmNoticeNode);
        _scanOrder.Add(skipConfirmOkNode);
        _scanOrder.Add(skipStoryNode);

        ConfigureNodes(_nodesByLabel);
    }

    /// <summary>
    /// 纯 configure:遍历 <paramref name="nodesByLabel"/>,按 label switch 装配 SuccessTemplate。
    /// 各节点的 Operation 在节点自己的 ctor 里配好(要读节点自己的状态/阈值)。
    ///
    /// 链:
    /// - SkipConfirmNotice → SkipConfirmOk(勾完「本次登录不再提示」再点「确认」)。
    /// - 其余节点命中后各自结束(SuccessTemplate = null),下一帧重新全节点扫描。
    /// </summary>
    private void ConfigureNodes(Dictionary<string, WorkflowNodeBase> nodesByLabel)
    {
        foreach (var (label, node) in nodesByLabel)
        {
            switch (label)
            {
                case "SkipConfirmNotice":
                    node.SuccessTemplate = nodesByLabel["SkipConfirmOk"];
                    break;

                default:
                    node.SuccessTemplate = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 每帧一次:
    /// 1) 有链在跑 → 推进一格(命中则执行 Operation 并切到 SuccessTemplate);
    /// 2) 没链在跑 → 全节点扫描,第一个命中的节点被激活并执行;
    /// 3) 都没命中 → 擦掉 overlay 上的框。
    /// </summary>
    public void OnCapture(TriggerCaptureContent content)
    {
        if (_current != null)
        {
            var node = _current;
            IWorkflowNode? next;
            try
            {
                next = node.MatchAndOperation(content, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "鸣潮工作流节点 {Label} 异常", node.Label);
                next = null;
            }

            _current = next;

            // 命中时基类(或节点自己)画了框 —— RetryCount 归零即命中。
            if (node.RetryCount == 0)
            {
                _rectDrawn = true;
            }

            if (_current != null)
            {
                // 链还在推进(弹窗模态,其他目标先不管)。
                return;
            }
        }

        // 全节点扫描:按优先级找第一个命中的目标。
        foreach (var node in _scanOrder)
        {
            if (!node.TryMatch(content, out var result))
            {
                continue;
            }

            try
            {
                // 命中:直接执行动作并进入链。
                // 不用 MatchAndOperation —— 它会把 TryMatch 再跑一遍,
                // 「跳过剧情」的固定区域模板匹配一帧跑两遍扛不住。
                node.Operation(content, result);
                _current = node.SuccessTemplate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "鸣潮工作流节点 {Label} 异常", node.Label);
                _current = null;
            }

            _rectDrawn = true;
            return;
        }

        // 一个都没命中就把框擦掉,否则上一次命中的框会一直留在屏幕上。
        if (_rectDrawn)
        {
            TriggerOverlayWindow.ClearMatchRect(_overlay);
            _rectDrawn = false;
        }
    }

    public void Dispose()
    {
        _current = null;

        foreach (var node in _nodesByLabel.Values)
        {
            node.Dispose();
        }

        _skipConfirmDetector.Dispose();
    }
}
