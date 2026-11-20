using System;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>「提示」弹窗里的两个可点目标。</summary>
public enum WuWaSkipConfirmRole
{
    /// <summary>「本次登录不再提示」勾选文本。</summary>
    Notice,

    /// <summary>「确认」按钮。</summary>
    Ok,
}

/// <summary>
/// 「提示」弹窗(跳过剧情确认框)的跨节点上下文。
///
/// 状态部分跨节点才有意义 —— 「只点一次勾选」「勾选后延迟点确认」「确认点击冷却」
/// 这三个约束都跨节点,状态放在节点里就断了。
///
/// 缓存部分是因为两个节点在同一帧里都要看弹窗,各跑一次模板匹配太浪费
/// (50ms 一帧的预算里多跑一遍就多占一份)。
/// </summary>
public sealed class WuWaSkipConfirmContext
{
    /// <summary>勾完「本次登录不再提示」之后,等多久再点「确认」。</summary>
    public static readonly TimeSpan OkClickDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>两次「确认」点击之间的最小间隔(点不掉时别每帧都点)。</summary>
    public static readonly TimeSpan OkClickCooldown = TimeSpan.FromMilliseconds(1000);

    private readonly object _lock = new();

    /// <summary>「本次登录不再提示」是否已经点过 —— 再点一次会取消勾选,只能点一次。</summary>
    public bool NoticeClicked { get; private set; }

    private DateTime _noticeClickedAt = DateTime.MinValue;

    private DateTime _lastOkClickAt = DateTime.MinValue;

    /// <summary>缓存所属的帧(按 Mat 实例认帧 —— 同一张图 = 同一帧)。</summary>
    private Mat? _cachedFrame;

    private WuWaSkipConfirmHit? _cachedNotice;

    private WuWaSkipConfirmHit? _cachedOk;

    /// <summary>
    /// 现在允不允许点「确认」:勾选后要等 <see cref="OkClickDelay"/>,
    /// 两次点击之间要隔 <see cref="OkClickCooldown"/>。
    /// </summary>
    public bool OkClickAllowed
    {
        get
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_noticeClickedAt != DateTime.MinValue && now < _noticeClickedAt + OkClickDelay)
                {
                    return false;
                }

                return now >= _lastOkClickAt + OkClickCooldown;
            }
        }
    }

    /// <summary>标记「本次登录不再提示」已点击(实际点击之前就要标,免得点击失败后又点一次)。</summary>
    public void MarkNoticeClicked()
    {
        lock (_lock)
        {
            NoticeClicked = true;
            _noticeClickedAt = DateTime.UtcNow;
        }
    }

    /// <summary>标记「确认」已点击,用于冷却计时。</summary>
    public void MarkOkClicked()
    {
        lock (_lock)
        {
            _lastOkClickAt = DateTime.UtcNow;
        }
    }

    /// <summary>弹窗消失时复位 —— 下次弹窗还能重新勾选。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            NoticeClicked = false;
            _noticeClickedAt = DateTime.MinValue;
            _lastOkClickAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 取本帧的弹窗检测结果。同一帧内多次调用只检测一次
    /// (两个节点共用,省掉一份模板匹配开销)。
    /// </summary>
    public (WuWaSkipConfirmHit? Notice, WuWaSkipConfirmHit? Ok) Detect(
        TriggerCaptureContent content,
        WuWaSkipConfirmDetector detector)
    {
        if (!ReferenceEquals(_cachedFrame, content.Image))
        {
            _cachedFrame = content.Image;
            _cachedNotice = detector.DetectNotice(content.Image, out _);
            _cachedOk = detector.DetectOk(content.Image, out _);
        }

        return (_cachedNotice, _cachedOk);
    }
}

/// <summary>
/// 「提示」弹窗(跳过剧情确认框)的工作流节点,两个角色:
/// <see cref="WuWaSkipConfirmRole.Notice"/>(勾选文本)和 <see cref="WuWaSkipConfirmRole.Ok"/>(确认按钮)。
///
/// 识别沿用 <see cref="WuWaSkipConfirmDetector"/> 的比例搜索带(不依赖固定分辨率),
/// 命中门槛用 detector 的识别门槛(0.75 / 0.85),**点击门槛另算**(0.90 / 0.95):
/// 识别到但分数不够高时只画框不点 —— 实机有第一帧冒出来的偏移匹配(0.910),
/// 点下去会落在胶囊底边,点不中。
///
/// 勾选节点的 SuccessTemplate 指向确认节点(链:勾选 → 确认),
/// 由触发器在 <c>ConfigureNodes</c> 里装配。
/// </summary>
public sealed class WuWaSkipConfirmWorkflowNode : WorkflowNodeBase
{
    /// <summary>「本次登录不再提示」的点击门槛(CCoeffNormed)。</summary>
    public const double NoticeClickThreshold = 0.90;

    /// <summary>「确认」的点击门槛(CCoeffNormed)。</summary>
    public const double OkClickThreshold = 0.95;

    private readonly WuWaSkipConfirmDetector _detector;

    private readonly WuWaSkipConfirmContext _context;

    private readonly WuWaSkipConfirmRole _role;

    private readonly ILogger _logger;

    /// <summary>最近一次命中的检测结果(画框 / 点击用)。</summary>
    private WuWaSkipConfirmHit? _hit;

    public WuWaSkipConfirmWorkflowNode(
        WuWaSkipConfirmRole role,
        int maxRetries,
        WuWaSkipConfirmDetector detector,
        WuWaSkipConfirmContext context,
        ILogger logger,
        TriggerOverlayWindow? overlay = null)
        : base(overlay)
    {
        _role = role;
        _detector = detector;
        _context = context;
        _logger = logger;

        Label = role == WuWaSkipConfirmRole.Notice ? "SkipConfirmNotice" : "SkipConfirmOk";
        MaxRetries = maxRetries;
        SuccessTemplate = null; // 由触发器装配:Notice → Ok
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

        var (noticeHit, okHit) = _context.Detect(content, _detector);

        if (noticeHit is null && okHit is null)
        {
            // 弹窗没了(点完「确认」或用户自己关掉)→ 复位勾选状态,下次弹窗还能重新勾。
            _context.Reset();
            _hit = null;
            return false;
        }

        var hit = _role == WuWaSkipConfirmRole.Notice ? noticeHit : okHit;
        if (hit is null)
        {
            _hit = null;
            return false;
        }

        if (_role == WuWaSkipConfirmRole.Notice)
        {
            // 已经勾过了 —— 再点一次会取消勾选,不再命中。
            if (_context.NoticeClicked)
            {
                _hit = null;
                return false;
            }
        }
        else if (!_context.OkClickAllowed)
        {
            // 勾选后 600ms 内 / 上次点击后 1s 内不点。
            _hit = null;
            return false;
        }

        _hit = hit;
        return true;
    }

    /// <summary>
    /// 命中动作:分数够高才点,否则只画框。
    /// </summary>
    private void OnHit(TriggerCaptureContent content, TemplateMatchResult? _)
    {
        var hit = _hit;
        if (hit is null)
        {
            return;
        }

        var label = _role == WuWaSkipConfirmRole.Notice ? "SkipConfirmNotice" : "SkipConfirmOk";
        TriggerOverlayWindow.DrawMatchRect(
            Overlay,
            new System.Drawing.Rectangle(hit.Rect.X, hit.Rect.Y, hit.Rect.Width, hit.Rect.Height),
            content,
            $"{label} {hit.Score:F3}");

        var threshold = _role == WuWaSkipConfirmRole.Notice ? NoticeClickThreshold : OkClickThreshold;
        if (hit.Score < threshold)
        {
            return;
        }

        var rect = hit.Rect;
        var x = rect.X + rect.Width / 2;
        var y = rect.Y + rect.Height / 2;

        if (_role == WuWaSkipConfirmRole.Notice)
        {
            _context.MarkNoticeClicked();
        }
        else
        {
            _context.MarkOkClicked();
        }

        var foreground = WuWaTriggerActions.ClickCapturePoint(content, x, y);
        _logger.LogInformation(
            "鸣潮跳过确认弹窗:已点击「{Text}」 位置=({X},{Y}) 得分={Score:F3} 方式={Way}",
            _role == WuWaSkipConfirmRole.Notice ? "本次登录不再提示" : "确认",
            x, y, hit.Score,
            foreground ? "前台SendInput" : "后台PostMessage(鸣潮在后台,UE不吃后台鼠标消息)");
    }

    public override void Dispose()
    {
        // 模板 Mat 由共享的 WuWaSkipConfirmDetector 持有(两个节点共用一个实例),这里不释放。
    }
}
