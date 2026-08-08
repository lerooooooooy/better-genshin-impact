using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv.TemplateMatch;
using BetterGenshinImpact.Core.Simulator;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using CvPoint = OpenCvSharp.Point;
using Vanara.PInvoke;
using IOPath = System.IO.Path;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 唯一 trigger：对每个模板做 ROI 匹配，score ≥ 阈值时：
/// 1. 调 onMatch 回调(→ overlay 画框 + DrawMatchAndSave 落盘整图)
/// 2. 调 onClick 回调(→ overlay 画红点)
/// 3. 同步前台 SendInput 点击(随机延迟 100~200ms 后真正落下)
/// </summary>
public sealed class EmptyZzzTaskTrigger : IZzzTaskTrigger
{
    public string Name => "EmptyZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    // 走 Global.Absolute(...) 拿到运行目录下的绝对路径。
    private const string TemplateFolder = "Assets\\Template";

    // <名字>_<x>_<y>_<w>x<h>(_<padding>).png
    // name 段允许 [A-Za-z0-9_\-]+，尾部用 _ 隔开数字；末尾 _<p> 为可选 padding 段。
    private static readonly Regex RoiNameRegex = new(
        @"^(?<name>[A-Za-z0-9_\-]+)_(?<x>\d+)_(?<y>\d+)_(?<w>\d+)x(?<h>\d+)(?:_(?<p>\d+))?$",
        RegexOptions.Compiled);

    // padding 段缺省 / 0 时的外扩像素数。
    private const int RoiPadding = 20;

    // CCoeffNormed 分数 ≥ 此值视为命中。
    private const double MatchSuccessThreshold = 0.99;

    // score < 1.0 都进 SaveDebug，便于排查。
    private const double DebugScoreThreshold = 1.0;

    private const int MaxDebugPerTemplate = 3;
    private const int MaxSuccessPerTemplate = 5;

    // 匹配成功后、真正点击前的随机延迟区间(取代了之前同模板/跨模板冷却)。
    private const int ClickPreDelayMinMs = 400;
    private const int ClickPreDelayMaxMs = 500;

    // 单个模板命中 → 真实点击后的冷却:期间同模板再次命中直接 return,不画框、不打点、不落盘、不点击。
    private const int SameTemplateCooldownMs = 3000;

    private readonly Action<DrawingRectangle, ZzzCaptureContent, string>? _onMatch;
    private readonly Action<DrawingPoint, ZzzCaptureContent>? _onClick;

    private List<TemplateEntry>? _templates;
    private readonly Dictionary<string, int> _debugSavedCount = new();
    private readonly Dictionary<string, int> _successSavedCount = new();

    // 同模板下次可点击的时间戳(stopwatch ms);name → tickMs。
    private readonly Dictionary<string, long> _nextClickTickMs = new();

    private sealed record TemplateEntry(string Name, CvRect CompactRoi, CvRect Roi, Mat Template);

    public EmptyZzzTaskTrigger(
        Action<DrawingRectangle, ZzzCaptureContent, string>? onMatch = null,
        Action<DrawingPoint, ZzzCaptureContent>? onClick = null)
    {
        _onMatch = onMatch;
        _onClick = onClick;
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        _templates ??= LoadTemplates();
        if (_templates.Count == 0)
        {
            return;
        }

        foreach (var template in _templates)
        {
            try
            {
                var safeRoi = ClampRoi(template.Roi, content.Image.Width, content.Image.Height);
                if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
                {
                    continue;
                }

                using var roi = new Mat(content.Image, safeRoi); // view，不复制像素
                var (loc, score) = TemplateMatchHelper.MatchTemplate(roi, template.Template, TemplateMatchModes.CCoeffNormed);

                if (score >= MatchSuccessThreshold)
                {
                    var absX = safeRoi.X + loc.X; // 用 clamped safeRoi，不是 CompactRoi
                    var absY = safeRoi.Y + loc.Y;
                    var matchRect = new CvRect(absX, absY, template.Template.Width, template.Template.Height);

                    // 同模板 4s 冷却:期间整次命中静默 — 不画框、不打标签、不落盘、不打红点、不点击。
                    // continue 而不是 break:本模板冷却中不影响后续模板在本帧继续判断。
                    var nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
                    if (_nextClickTickMs.TryGetValue(template.Name, out var nextMs) && nowMs < nextMs)
                    {
                        continue;
                    }

                    // 守门通过 → 立刻写冷却(按"决定点击"时刻起算,而不是 SendInput 完成时刻)。
                    _nextClickTickMs[template.Name] = nowMs + SameTemplateCooldownMs;

                    _onMatch?.Invoke(new DrawingRectangle(matchRect.X, matchRect.Y, matchRect.Width, matchRect.Height), content, template.Name);
                    DrawMatchAndSave(content, template, absX, absY, score);
                    TryClickOnMatch(template.Name, matchRect, content);
                    break; // ★ 帧内首匹配胜出
                }

                if (score < DebugScoreThreshold)
                {
                    SaveDebug(content, template, roi, loc, score);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ] {template.Name} OnCapture error: {ex.Message}");
            }
        }
    }

    private List<TemplateEntry> LoadTemplates()
    {
        var list = new List<TemplateEntry>();
        var folder = Global.Absolute(TemplateFolder);
        Debug.WriteLine($"[ZZZ] scanning template folder: {folder}");

        if (!System.IO.Directory.Exists(folder))
        {
            Debug.WriteLine("[ZZZ] templates loaded: 0 (folder missing)");
            return list;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.png"))
        {
            var fileName = IOPath.GetFileNameWithoutExtension(file);
            var m = RoiNameRegex.Match(fileName);
            if (!m.Success)
            {
                // 目录里可以放别的图，不会报错。
                continue;
            }

            var name = m.Groups["name"].Value;
            var x = int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
            var y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            var w = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);

            var padding = RoiPadding;
            if (m.Groups["p"].Success)
            {
                var p = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
                if (p > 0)
                {
                    padding = p;
                }
            }

            Mat template;
            try
            {
                template = new Mat(file, ImreadModes.Color);
                if (template.Empty())
                {
                    template.Dispose();
                    continue;
                }
            }
            catch
            {
                continue;
            }

            var compactRoi = new CvRect(x, y, w, h);
            var roi = new CvRect(x - padding, y - padding, w + padding * 2, h + padding * 2);
            list.Add(new TemplateEntry(name, compactRoi, roi, template));
        }

        Debug.WriteLine($"[ZZZ] templates loaded: {list.Count}");
        return list;
    }

    /// <summary>
    /// 把可能越界的 Roi 钳制到当前帧的 [0, W) × [0, H) 范围，完全越界时退回整个图片范围。
    /// </summary>
    private static CvRect ClampRoi(CvRect r, int width, int height)
    {
        var x1 = Math.Max(0, r.X);
        var y1 = Math.Max(0, r.Y);
        var x2 = Math.Min(width, r.X + r.Width);
        var y2 = Math.Min(height, r.Y + r.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return new CvRect(0, 0, width, height);
        }

        return new CvRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// 同步前台 SendInput。冷却守门在 OnCapture 里(进入此方法前已通过),此处不再校验。
    /// 不在此处调 ActivateWindow —— 窗口前台切换统一由 dispatcher.Start 兜底,避免每次命中都抢焦点
    /// 打断用户操作(对齐原神 TaskTriggerDispatcher.Start 的做法)。要求 ZZZ 启动时在前台,
    /// 期间用户切走会导致 SendInput 落到 BetterGI(与原神一致的用户行为约束)。
    /// 锁由 dispatcher 的 Monitor.TryEnter 把守。
    /// </summary>
    private void TryClickOnMatch(string name, CvRect matchRect, ZzzCaptureContent content)
    {
        var rx = matchRect.X + Random.Shared.Next(0, Math.Max(1, matchRect.Width));
        var ry = matchRect.Y + Random.Shared.Next(0, Math.Max(1, matchRect.Height));

        _onClick?.Invoke(new DrawingPoint(rx, ry), content);

        var preDelayMs = Random.Shared.Next(ClickPreDelayMinMs, ClickPreDelayMaxMs + 1);
        Thread.Sleep(preDelayMs);

        var captureRect = content.CaptureRect;
        var screenX = rx + (captureRect?.Left ?? 0);
        var screenY = ry + (captureRect?.Top ?? 0);
        var screenWidth = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var screenHeight = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        var absX = screenX * 65535.0 / (screenWidth - 1);
        var absY = screenY * 65535.0 / (screenHeight - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
        Debug.WriteLine($"[ZZZ] click {name} at screen=({screenX},{screenY}) preDelay={preDelayMs}ms");
    }

    private void DrawMatchAndSave(ZzzCaptureContent content, TemplateEntry template, int absX, int absY, double score)
    {
        var count = _successSavedCount.GetValueOrDefault(template.Name, 0);
        var matchRect = new CvRect(absX, absY, template.Template.Width, template.Template.Height);

        if (count >= MaxSuccessPerTemplate)
        {
            return;
        }

        try
        {
            using var annotated = content.Image.Clone();
            Cv2.Rectangle(annotated, matchRect, new Scalar(0, 255, 0), 3);
            Cv2.PutText(annotated, $"{template.Name} {score:F3}", new CvPoint(matchRect.X, Math.Max(0, matchRect.Y - 5)),
                HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 255, 0), 2);

            var dir = Global.Absolute($"log\\zzz-success-{DateTime.Now:HHmmssfff}");
            System.IO.Directory.CreateDirectory(dir);
            var path = IOPath.Combine(dir, $"{count}-{template.Name}-{score:F3}.png");
            Cv2.ImWrite(path, annotated);
            _successSavedCount[template.Name] = count + 1;
            Debug.WriteLine($"[ZZZ-success] {template.Name} score={score:F3} absRect=({matchRect.X},{matchRect.Y},{matchRect.Width},{matchRect.Height}) -> {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-success] save error: {ex.Message}");
        }
    }

    private void SaveDebug(ZzzCaptureContent content, TemplateEntry template, Mat roi, CvPoint loc, double score)
    {
        var count = _debugSavedCount.GetValueOrDefault(template.Name, 0);
        if (count >= MaxDebugPerTemplate)
        {
            return;
        }

        try
        {
            var dir = Global.Absolute($"log\\zzz-debug-{DateTime.Now:HHmmssfff}");
            System.IO.Directory.CreateDirectory(dir);
            var prefix = IOPath.Combine(dir, $"{count}-{template.Name}-{score:F3}");
            Cv2.ImWrite($"{prefix}-frame.png", content.Image);
            Cv2.ImWrite($"{prefix}-roi.png", roi);
            Cv2.ImWrite($"{prefix}-template.png", template.Template);
            _debugSavedCount[template.Name] = count + 1;
            Debug.WriteLine($"[ZZZ-debug] {template.Name} score={score:F3} loc=({loc.X},{loc.Y}) -> {prefix}-*.png");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-debug] save error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_templates != null)
        {
            foreach (var t in _templates)
            {
                t.Template.Dispose();
            }

            _templates = null;
        }
    }
}
