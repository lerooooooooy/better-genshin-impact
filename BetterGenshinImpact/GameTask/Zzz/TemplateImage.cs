using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv.TemplateMatch;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 模板匹配结果,由 <see cref="TemplateImage.TryMatch"/> 产出。
/// </summary>
public sealed record TemplateMatchResult(
    /// <summary>钳制到图像边界后的实际搜索区域,可能小于 <see cref="TemplateImage.SearchRoi"/>;完全越界时退回整个图片范围。</summary>
    CvRect ClampedSearchRoi,
    /// <summary>匹配位置,坐标相对于 ClampedSearchRoi 左上角(不是全图)。</summary>
    CvPoint Loc,
    /// <summary>CCoeffNormed 分数,范围 0~1,越接近 1 越匹配;命中判定由调用方按 <see cref="TemplateImage.Threshold"/> 判断。</summary>
    double Score,
    /// <summary>全图绝对矩形(ClampedSearchRoi 偏移 + Loc + 模板尺寸),可直接用于点击坐标。</summary>
    CvRect AbsRect);

/// <summary>
/// 单个模板图片对应的实体。
/// 文件名约定:&lt;name&gt;_&lt;x&gt;_&lt;y&gt;_&lt;w&gt;x&lt;h&gt;(_&lt;padding&gt;).png
/// 例:menuBar_1638_112_169x40.png 或 skipRT_1627_103_234x61_200.png
/// </summary>
public sealed record TemplateImage(
    string Name,
    CvRect Roi,
    int Padding,
    double Threshold,
    Mat Template)
{
    /// <summary>
    /// 实际搜索区域(向 Roi 外扩 Padding 像素)。
    /// </summary>
    public CvRect SearchRoi => new CvRect(
        Roi.X - Padding,
        Roi.Y - Padding,
        Roi.Width + Padding * 2,
        Roi.Height + Padding * 2);

    public const int DefaultPadding = 20;
    public const double DefaultThreshold = 0.99;

    // name 段允许 [A-Za-z0-9_\-]+,
    // 末尾 _<p> 为可选 padding 段(>0 用之,=0/缺省回落 DefaultPadding)。
    private static readonly Regex FileNameRegex = new(
        @"^(?<name>[A-Za-z0-9_\-]+)_(?<x>\d+)_(?<y>\d+)_(?<w>\d+)x(?<h>\d+)(?:_(?<p>\d+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 从单个 PNG 文件构建 TemplateImage 实体。文件名必须匹配约定,否则抛 ArgumentException。
    /// </summary>
    /// <param name="filePath">模板图片绝对路径</param>
    /// <param name="defaultPadding">文件名无 padding 段或为 0 时回落的默认值</param>
    /// <param name="defaultThreshold">模版匹配阈值(CCoeffNormed)</param>
    /// <exception cref="FileNotFoundException">文件不存在</exception>
    /// <exception cref="ArgumentException">文件名不符合约定</exception>
    /// <exception cref="InvalidOperationException">OpenCV 读取失败或图像为空</exception>
    public static TemplateImage FromFile(
        string filePath,
        double defaultThreshold = DefaultThreshold,
        int defaultPadding = DefaultPadding)
    {
        var path = Global.Absolute(filePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("模板文件不存在", filePath);
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var m = FileNameRegex.Match(fileName);
        if (!m.Success)
        {
            throw new ArgumentException(
                $"模板文件名不符合约定: {fileName} (期望 <name>_<x>_<y>_<w>x<h>(_<padding>).png)",
                nameof(filePath));
        }

        var name = m.Groups["name"].Value;
        var x = int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
        var y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        var w = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);

        var padding = defaultPadding;
        if (m.Groups["p"].Success)
        {
            var p = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
            if (p > 0)
            {
                padding = p;
            }
        }

        var template = new Mat(filePath, ImreadModes.Color);
        if (template.Empty())
        {
            template.Dispose();
            throw new InvalidOperationException($"模板图像加载失败或为空: {filePath}");
        }

        return new TemplateImage(name, new CvRect(x, y, w, h), padding, defaultThreshold, template);
    }

    /// <summary>
    /// 列出指定目录下所有 <c>*.png</c> 模板文件的绝对路径(返回的是 <see cref="Global.Absolute"/> 解析后的路径,
    /// 调用方可直接喂给 <see cref="TemplateWaitTarget"/> / <see cref="TemplateWorkflowNode"/> 等)。
    /// 按文件名 Ordinal 排序(确定性顺序,跨平台一致)。
    /// 目录不存在时返回空列表(不抛异常,方便 trigger 启动时容错)。
    /// </summary>
    /// <param name="dirRelativePath">相对 <see cref="Global.StartUpPath"/> 的目录路径(走 <see cref="Global.Absolute"/>)</param>
    public static IReadOnlyList<string> EnumerateTemplateFiles(string dirRelativePath)
    {
        if (string.IsNullOrEmpty(dirRelativePath))
        {
            throw new ArgumentException("dirRelativePath required", nameof(dirRelativePath));
        }

        var dir = Global.Absolute(dirRelativePath);
        if (!Directory.Exists(dir))
        {
            Debug.WriteLine($"[ZZZ-Template] directory not found: {dir}");
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(dir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// 在 content.Image 上做 CCoeffNormed 模板匹配。
    /// 始终在 SearchRoi(外扩 Padding 后)上做匹配,越界自动钳制,完全越界退回整个图片范围。
    /// 不会向外抛异常:匹配失败时返回 Score=0 的默认结果。
    /// </summary>
    public TemplateMatchResult TryMatch(ZzzCaptureContent content)
    {
        var safeRoi = ClampRoi(SearchRoi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            return new TemplateMatchResult(safeRoi, default, 0, default);
        }

        try
        {
            using var roi = new Mat(content.Image, safeRoi);
            var (loc, score) = TemplateMatchHelper.MatchTemplate(roi, Template, TemplateMatchModes.CCoeffNormed);
            var absRect = new CvRect(
                safeRoi.X + loc.X,
                safeRoi.Y + loc.Y,
                Template.Width,
                Template.Height);
            return new TemplateMatchResult(safeRoi, loc, score, absRect);
        }
        catch
        {
            return new TemplateMatchResult(safeRoi, default, 0, default);
        }
    }

    /// <summary>
    /// 把可能越界的 Roi 钳制到当前帧的 [0, W) × [0, H) 范围,完全越界时退回整个图片范围。
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
}
