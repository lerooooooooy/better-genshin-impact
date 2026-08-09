using System;
using System.Diagnostics;
using System.Threading;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// HSV 颜色掩膜版的"等待出现"流程。与 <see cref="TemplateOverlayRunner"/> 兄弟并列,
/// 同样的轮询骨架(总时长预算 / capture 失败不计数 / try/finally Dispose content),
/// 区别只在命中判定:本类由调用方注入 detector,而不是模板匹配。
///
/// 适用场景:无模板图、靠颜色特征识别的对象 —— 例如继续对话箭头 »»
/// (半透明白灰,HSV 低饱和 + 高亮度)。
/// </summary>
public static class HsvOverlayRunner
{
    /// <summary>
    /// 在总时长 <paramref name="totalTimeoutMs"/> 内最多尝试 <paramref name="maxAttempts"/> 次,
    /// 每次通过 <paramref name="captureProvider"/> 拿新一帧,跑 <paramref name="detector"/>
    /// 返回 true 即视为命中。
    ///
    /// 捕获失败(<paramref name="captureProvider"/> 返回 null)不计入尝试次数、不消耗 sleep。
    ///
    /// 命中分支:<paramref name="onHit"/> 回调以 (ZzzCaptureContent) 调用,content 在回调内仍有效;
    /// 回调返回后 helper 才 Dispose content。
    /// 未命中 / 全部尝试捕获失败 → 返回 null。
    /// </summary>
    /// <param name="captureProvider">每次尝试调一次,返回 null 表示本帧截图失败(最小化/失焦)。不可为 null。</param>
    /// <param name="detector">单帧命中判定,返回 true 即视为命中。不可为 null。</param>
    /// <param name="onHit">命中时调用一次的回调;null 表示不处理命中</param>
    /// <param name="maxAttempts">最大尝试次数,默认 3</param>
    /// <param name="totalTimeoutMs">总超时上限(毫秒),默认 3000</param>
    /// <returns>命中时返回该 content;未命中返回 null</returns>
    /// <exception cref="ArgumentNullException">captureProvider 或 detector 为 null 时抛出</exception>
    public static ZzzCaptureContent? WaitForHsvAppear(
        Func<ZzzCaptureContent?> captureProvider,
        Func<ZzzCaptureContent, bool> detector,
        Action<ZzzCaptureContent>? onHit = null,
        int maxAttempts = 3,
        int totalTimeoutMs = 3000)
    {
        if (captureProvider == null)
        {
            throw new ArgumentNullException(nameof(captureProvider));
        }
        if (detector == null)
        {
            throw new ArgumentNullException(nameof(detector));
        }
        if (maxAttempts < 1 || totalTimeoutMs <= 0)
        {
            return null;
        }

        var intervalMs = maxAttempts > 1 ? totalTimeoutMs / (maxAttempts - 1) : totalTimeoutMs;
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
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture provider threw: {ex.Message}");
                content = null;
            }

            if (content == null)
            {
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture returned null (minimized?)");
            }
            else
            {
                bool hit;
                try
                {
                    hit = detector(content);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: detector threw: {ex.GetType().Name}: {ex.Message}");
                    content.Dispose();
                    continue;
                }

                if (hit)
                {
                    try
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: HSV HIT");
                        onHit?.Invoke(content);
                    }
                    finally
                    {
                        content.Dispose();
                    }
                    return content;
                }

                content.Dispose();
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: HSV miss");
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(intervalMs);
            }
        }

        var totalMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency - startMs;
        Debug.WriteLine($"[ZZZ-Wait] HSV budget exhausted: attempts={maxAttempts} elapsed={totalMs}ms");
        return null;
    }
}
