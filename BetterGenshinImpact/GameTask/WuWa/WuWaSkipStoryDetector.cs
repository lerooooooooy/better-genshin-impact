using System;
using System.IO;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv.TemplateMatch;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮「跳过剧情」按钮的识别结果。
/// </summary>
public sealed class WuWaSkipStoryButton
{
    /// <summary>「跳过剧情」文字区域(绝对坐标,capture 坐标系)。</summary>
    public CvRect Rect { get; init; }

    /// <summary>模板匹配得分(CCoeffNormed)。</summary>
    public double Score { get; init; }
}

/// <summary>
/// 「跳过剧情」按钮识别:固定尺寸模板,搜索范围限定在「剧情快览」面板右下角一块固定区域。
///
/// 模板是按钮里那四个字(不含胶囊底),87x22。
/// </summary>
public sealed class WuWaSkipStoryDetector : IDisposable
{
    private const string TemplatePath = @"GameTask\WuWa\tem\skipStory_1171_695_87x22.png";

    /// <summary>
    /// 「跳过剧情」按钮的搜索区域(绝对坐标,capture 坐标系,1920x1080 基准)。
    ///
    /// 原来是整个画面右半屏(x ∈ [960,1920] 全高),每帧要扫 960x1080,
    /// 既慢又容易匹配到面板里的其它文字。现在只扫按钮真正所在的这块。
    /// 实机命中位置 (1242,749) 落在区域内。
    ///
    /// 跟模板一样是**绝对坐标** —— 换分辨率就对不上(模板本身也是定尺的)。
    /// </summary>
    private static readonly CvRect SearchRoi = new(1058, 515, 358, 416);

    /// <summary>
    /// 命中阈值(CCoeffNormed)。低于这个分就不算命中,连框都不画。
    /// 调低这个值可以看到所有候选位置及其得分,方便观察识别质量。
    /// </summary>
    public const double MatchThreshold = 0.75;

    private Mat? _template;

    /// <summary>
    /// 在 <see cref="SearchRoi"/> 里找「跳过剧情」按钮。找不到返回 null,bestScore 给出本次的最高得分。
    /// </summary>
    public WuWaSkipStoryButton? Detect(Mat frame, out double bestScore)
    {
        bestScore = 0;
        if (!TryGetTemplate())
        {
            return null;
        }

        // 自己算交集:ImageUtils.ClampRoi 在完全越界时会退回整帧,
        // 这里宁可匹配不上,也不能把搜索区悄悄放大成全屏。
        var x1 = Math.Max(0, SearchRoi.X);
        var y1 = Math.Max(0, SearchRoi.Y);
        var x2 = Math.Min(frame.Width, SearchRoi.X + SearchRoi.Width);
        var y2 = Math.Min(frame.Height, SearchRoi.Y + SearchRoi.Height);
        if (x2 - x1 < _template.Width || y2 - y1 < _template.Height)
        {
            return null;
        }

        var roi = new CvRect(x1, y1, x2 - x1, y2 - y1);
        using var sub = new Mat(frame, roi);
        var (loc, score) = TemplateMatchHelper.MatchTemplate(sub, _template, TemplateMatchModes.CCoeffNormed);
        bestScore = score;
        if (score < MatchThreshold)
        {
            return null;
        }

        return new WuWaSkipStoryButton
        {
            Rect = new CvRect(roi.X + loc.X, roi.Y + loc.Y, _template.Width, _template.Height),
            Score = score,
        };
    }

    private bool TryGetTemplate()
    {
        if (_template != null)
        {
            return true;
        }

        try
        {
            var path = Global.Absolute(TemplatePath);
            if (!File.Exists(path))
            {
                return false;
            }

            var mat = new Mat(path, ImreadModes.Color);
            if (mat.Empty())
            {
                mat.Dispose();
                return false;
            }

            _template = mat;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _template?.Dispose();
        _template = null;
    }
}
