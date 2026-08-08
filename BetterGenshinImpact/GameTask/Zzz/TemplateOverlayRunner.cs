using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using DrawingRectangle = System.Drawing.Rectangle;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 模板匹配 + 命中画绿框的统一静态流程。
/// 触发器每帧调一次 <see cref="TryMatchAndDraw"/>，由本类负责懒加载模板、按阈值匹配、命中判定、命中画框。
/// trigger 自身只需持有 template 缓存与 overlay 引用。
/// </summary>
public static class TemplateOverlayRunner
{
    /// <summary>
    /// 单帧匹配流程：懒加载模板 → 匹配 → 命中判定 → 命中画框。
    /// </summary>
    /// <param name="content">当帧截图 + captureRect/dpiScale 上下文</param>
    /// <param name="overlay">null 时跳过画框但仍走匹配</param>
    /// <param name="templateRelativePath">模板相对路径（由 <see cref="TemplateImage.FromFile"/> 走 Global.Absolute）</param>
    /// <param name="template">[in/out] 模板缓存；首次调用为 null，由本方法懒加载</param>
    /// <param name="threshold">命中阈值；默认 <see cref="TemplateImage.DefaultThreshold"/></param>
    /// <returns>匹配结果：null=加载或匹配异常；非 null 表示匹配已完成（score ≥ 0，由 caller 自行判定是否命中；命中分支内部已派发画框任务）</returns>
    public static TemplateMatchResult? TryMatchAndDraw(
        ZzzCaptureContent content,
        ZzzOverlayWindow? overlay,
        string templateRelativePath,
        ref TemplateImage? template,
        double threshold = TemplateImage.DefaultThreshold)
    {
        try
        {
            if (template == null)
            {
                template = TemplateImage.FromFile(templateRelativePath, threshold);
                Debug.WriteLine($"[ZZZ-Template] loaded template name={template.Name} roi=({template.Roi.X},{template.Roi.Y},{template.Roi.Width},{template.Roi.Height}) padding={template.Padding} threshold={template.Threshold:F2}");
            }

            var result = template.TryMatch(content);

            if (result.Score >= template.Threshold)
            {
                var abs = result.AbsRect;
                var rect = new DrawingRectangle(abs.X, abs.Y, abs.Width, abs.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(overlay, rect, content, $"{template.Name} score={result.Score:F3}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Template] template load/match failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 等待模板图片出现。在总时长 <paramref name="totalTimeoutMs"/> 内最多尝试 <paramref name="maxAttempts"/> 次,
    /// 每次尝试通过 <paramref name="captureProvider"/> 拿新一帧,匹配 score ≥ <paramref name="threshold"/> 即视为命中。
    /// 默认 3 次 / 3000ms,节拍近似均匀(interval = totalTimeoutMs / (maxAttempts - 1))。
    ///
    /// 捕获失败(<paramref name="captureProvider"/> 返回 null)不计入尝试次数、不消耗 sleep。
    ///
    /// 命中分支:<paramref name="onHit"/> 回调以 (TemplateMatchResult, ZzzCaptureContent) 调用,
    /// content 在回调内仍有效;回调返回后 helper 才 Dispose content。
    /// 未命中 / 模板加载失败 / 全部尝试捕获失败 → 返回 null。
    /// </summary>
    /// <param name="captureProvider">每次尝试调一次,返回 null 表示本帧截图失败(最小化/失焦)。不可为 null。</param>
    /// <param name="templateRelativePath">模板相对路径(走 <see cref="TemplateImage.FromFile"/>)</param>
    /// <param name="template">[in/out] 模板缓存;首次调用为 null 时懒加载</param>
    /// <param name="onHit">命中时调用一次的回调;content 在回调内有效,null 表示不处理命中</param>
    /// <param name="threshold">命中阈值,默认 <see cref="TemplateImage.DefaultThreshold"/></param>
    /// <param name="maxAttempts">最大尝试次数,默认 3</param>
    /// <param name="totalTimeoutMs">总超时上限(毫秒),默认 3000</param>
    /// <returns>命中时返回 TemplateMatchResult(score ≥ threshold);未命中返回 null</returns>
    /// <exception cref="ArgumentNullException">captureProvider 为 null 时抛出</exception>
    public static TemplateMatchResult? WaitForTemplateAppear(
        Func<ZzzCaptureContent?> captureProvider,
        string templateRelativePath,
        ref TemplateImage? template,
        Action<TemplateMatchResult, ZzzCaptureContent>? onHit = null,
        double threshold = TemplateImage.DefaultThreshold,
        int maxAttempts = 3,
        int totalTimeoutMs = 3000)
    {
        if (captureProvider == null)
        {
            throw new ArgumentNullException(nameof(captureProvider));
        }

        if (maxAttempts < 1 || totalTimeoutMs <= 0)
        {
            return null;
        }

        try
        {
            template ??= TemplateImage.FromFile(templateRelativePath, threshold);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Wait] template load failed: {ex.Message}");
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
                var result = template.TryMatch(content);
                if (result.Score >= threshold)
                {
                    try
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: HIT score={result.Score:F3}");
                        onHit?.Invoke(result, content);
                    }
                    finally
                    {
                        content.Dispose();
                    }
                    return result;
                }

                content.Dispose();
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: miss score={result.Score:F3}");
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(intervalMs);
            }
        }

        var totalMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency - startMs;
        Debug.WriteLine($"[ZZZ-Wait] budget exhausted: attempts={maxAttempts} elapsed={totalMs}ms");
        return null;
    }
}