using System;
using System.Diagnostics;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮「跳过剧情」文字按钮的工作流节点:命中后后台点击命中区域。
///
/// 识别门槛用 <see cref="WuWaSkipStoryDetector.MatchThreshold"/>(0.75,低门槛是为了把所有候选都画出来看得分),
/// 点击门槛另算(0.90):分数不够高只画框不点,避免误点。
///
/// 画框自己画(不借基类的自动画框):命中结果存在字段里,Operation 里画,
/// 这样触发器扫描到命中后可以直接执行 Operation,不用再跑一次检测 ——
/// 固定区域的模板匹配一帧跑两遍扛不住。
/// </summary>
public sealed class WuWaSkipStoryWorkflowNode : WorkflowNodeBase
{
    /// <summary>后台点击的门槛(CCoeffNormed)。</summary>
    private const double ClickThreshold = 0.90;

    /// <summary>连发后台点击的最小间隔(毫秒)。按钮没被游戏消费掉时会一直匹配上,不限速的话每帧都会点一次。</summary>
    private const int ClickCooldownMs = 1000;

    /// <summary>写匹配得分的最小间隔(毫秒)。同一目标会连续多帧命中,不限速会刷爆日志。</summary>
    private const int ScoreLogCooldownMs = 1000;

    private readonly WuWaSkipStoryDetector _detector = new();

    private readonly ILogger _logger;

    /// <summary>搜索区域匹配的历史最高得分,用于观察实际能到多少分。</summary>
    private double _bestScore;

    /// <summary>打日志的计数(每 N 次记一次得分)。</summary>
    private int _bestScoreCounter;

    /// <summary>上次投递后台点击的时刻(Stopwatch 时间戳)。</summary>
    private long _lastClickTimestamp;

    /// <summary>上次写匹配得分的时刻(Stopwatch 时间戳)。</summary>
    private long _lastScoreLogTimestamp;

    /// <summary>最近一次命中的检测结果(画框 / 点击用)。</summary>
    private WuWaSkipStoryButton? _hit;

    public WuWaSkipStoryWorkflowNode(int maxRetries, ILogger logger, TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        _logger = logger;

        Label = "SkipStory";
        MaxRetries = maxRetries;
        SuccessTemplate = null;
        Operation = OnHit;
    }

    public override string Label { get; }

    public override int MaxRetries { get; internal set; }

    public override IWorkflowNode? SuccessTemplate { get; internal set; }

    public override IWorkflowNode? FailTemplate { get; internal set; }

    public override Action<TriggerCaptureContent, TemplateMatchResult?> Operation { get; internal set; }

    public override bool TryMatch(TriggerCaptureContent content, out TemplateMatchResult? result)
    {
        // 结果不是模板匹配对象 —— 画框在 Operation 里自己做。
        result = null;

        _hit = _detector.Detect(content.Image, out var bestScore);
        if (_hit is null)
        {
            _bestScore = Math.Max(_bestScore, bestScore);
            if (++_bestScoreCounter % 100 == 0)
            {
                _logger.LogInformation("鸣潮跳过剧情:最高得分={Score:F3}(本次 {Cur:F3},阈值 {Threshold:F2})",
                    _bestScore, bestScore, WuWaSkipStoryDetector.MatchThreshold);
            }

            return false;
        }

        return true;
    }

    private void OnHit(TriggerCaptureContent content, TemplateMatchResult? _)
    {
        var hit = _hit;
        if (hit is null)
        {
            return;
        }

        TriggerOverlayWindow.DrawMatchRect(
            Overlay,
            new System.Drawing.Rectangle(hit.Rect.X, hit.Rect.Y, hit.Rect.Width, hit.Rect.Height),
            content,
            $"SkipStory {hit.Score:F3}");

        var now = Stopwatch.GetTimestamp();

        // 得分每秒写一条,方便观察识别质量(同一目标会连续多帧命中)。
        if ((now - _lastScoreLogTimestamp) * 1000.0 / Stopwatch.Frequency >= ScoreLogCooldownMs)
        {
            _lastScoreLogTimestamp = now;
            _logger.LogInformation("鸣潮跳过剧情: 位置=({X},{Y}) 得分={Score:F3}", hit.Rect.X, hit.Rect.Y, hit.Score);
        }

        // 分数不够高只画框,不点。
        if (hit.Score < ClickThreshold)
        {
            return;
        }

        if ((now - _lastClickTimestamp) * 1000.0 / Stopwatch.Frequency < ClickCooldownMs)
        {
            return;
        }

        _lastClickTimestamp = now;
        WuWaTriggerActions.ClickRectBackground(hit.Rect, content);
        _logger.LogInformation("鸣潮跳过剧情按钮命中: 位置=({X},{Y}) 得分={Score:F3},已后台点击",
            hit.Rect.X, hit.Rect.Y, hit.Score);
    }

    public override void Dispose() => _detector.Dispose();
}
