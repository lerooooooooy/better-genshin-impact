using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// ZZZ 子系统共享的"轮询等待命中"工具。把 <see cref="IZzzWaitTarget"/> 列表 + capture provider
/// 在固定 <c>intervalMs</c> 内反复跑,任一命中即返回(命中帧泄漏给 caller,中间帧内部 Dispose)。
///
/// 用于 TestZzzTaskTrigger 各 seq 的 WaitForHit、启动后等「点击进入游戏」OCR 按钮等场景。
/// 时间预算由 <c>totalTimeoutMs</c> 与 <c>intervalMs</c> 独立控制:
/// <c>maxAttempts = ceil(totalTimeoutMs / intervalMs) + 1</c>,与原 TestZzzTaskTrigger.WaitForHit
/// 的 <c>(totalTimeoutMs=3000, maxAttempts=3)</c> 等价时 <c>intervalMs=1500</c>(保留 3000ms 总预算 + 1500ms 间隔语义)。
///
/// 返回值:
/// <list type="bullet">
///   <item><c>matchedLabel</c> == null → 全部未命中 / capture 全失败 / 超时</item>
///   <item><c>TemplateMatchResult?</c> 非 null → 模板命中(给画框 / 点击坐标用)</item>
///   <item><c>TemplateMatchResult?</c> 为 null → HSV 或 OCR 命中(caller 走 ROI 画框 / 固定点击区)</item>
///   <item><c>ZzzCaptureContent?</c> 非 null → 命中帧,caller 必须 Dispose</item>
/// </list>
///
/// 同步阻塞:单次调用最长占用 <c>totalTimeoutMs</c>。
/// </summary>
public static class ZzzWaitForHit
{
    public static (string? matchedLabel, TemplateMatchResult? result, ZzzCaptureContent? content) Run(
        int totalTimeoutMs,
        int intervalMs,
        Func<ZzzCaptureContent?> captureProvider,
        params IZzzWaitTarget[] targets)
    {
        if (targets == null || targets.Length == 0)
        {
            return (null, null, null);
        }

        if (totalTimeoutMs <= 0)
        {
            return (null, null, null);
        }

        // 与旧 API 兼容:totalTimeoutMs=3000, maxAttempts=3 等价于 totalTimeoutMs=3000, intervalMs=1500
        // (maxAttempts=ceil(3000/1500)+1 = 3)。
        var maxAttempts = intervalMs > 0
            ? Math.Max(1, (int)Math.Ceiling((double)totalTimeoutMs / intervalMs) + 1)
            : 1;

        var startMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        var deadlineMs = startMs + totalTimeoutMs;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            if (nowMs > deadlineMs)
            {
                break;
            }

            ZzzCaptureContent? content;
            try
            {
                content = captureProvider();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture provider threw: {ex.GetType().Name}: {ex.Message}");
                content = null;
            }

            if (content == null)
            {
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture returned null (minimized?)");
            }
            else
            {
                foreach (var target in targets)
                {
                    bool hit;
                    TemplateMatchResult? matchResult;
                    try
                    {
                        hit = target.TryMatch(content, out matchResult);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: {target.Label} TryMatch threw: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (hit)
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: HIT type={(matchResult != null ? "template" : "hsv")} label={target.Label}{FormatMatchDetail(matchResult, target.Threshold)}");
                        return (target.Label, matchResult, content);
                    }

                    // per-target miss log:score + threshold 直观看离命中差多远
                    Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: {target.Label} miss{FormatMatchDetail(matchResult, target.Threshold)}");
                }

                content.Dispose();
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(intervalMs);
            }
        }

        var totalMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency - startMs;
        Debug.WriteLine($"[ZZZ-Wait] budget exhausted: attempts={maxAttempts} elapsed={totalMs}ms targets=[{string.Join(",", targets.Select(t => t.Label))}]");
        return (null, null, null);
    }

    /// <summary>
    /// 格式化 score + threshold 给日志用。
    /// 模板命中/未命中 → " score=0.583 threshold=0.960";HSV / OCR → " hsv detector"。
    /// 公开给同命名空间其它 trigger(如 TestZzzTaskTrigger.ScanAllMainTemplates)复用日志格式。
    /// </summary>
    public static string FormatMatchDetail(TemplateMatchResult? result, double? threshold)
    {
        var s = result != null ? $" score={result.Score:F3}" : "";
        var t = threshold.HasValue ? $" threshold={threshold.Value:F3}" : " hsv detector";
        return $"{s}{t}";
    }
}
