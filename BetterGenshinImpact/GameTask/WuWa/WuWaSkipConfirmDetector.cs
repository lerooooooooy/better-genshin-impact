using System;
using System.IO;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv.TemplateMatch;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮「提示」弹窗(跳过剧情确认框)的识别结果。
/// </summary>
public sealed class WuWaSkipConfirmHit
{
    /// <summary>命中区域(绝对坐标,capture 坐标系)。</summary>
    public CvRect Rect { get; init; }

    /// <summary>模板匹配得分(CCoeffNormed)。</summary>
    public double Score { get; init; }
}

/// <summary>
/// 「提示」弹窗识别:两个固定尺寸模板 + 两个按比例给的搜索带。
///
/// 弹窗内容(1920x1080 抓帧):
/// - 标题「提示」,正文「完整观看剧情更有助于了解故事发展,是否确认跳过?」
/// - 白色圆点(未勾选) + 「本次登录不再提示」 —— 文字在 x 900..1042 / y 572..592
/// - 黑色胶囊「取消」(x 593..852)与「确认」(x 1161..1210 的两个字 / 胶囊到 x 1314)
///
/// 模板是定尺的,鸣潮 UI 会随渲染分辨率整体缩放,换分辨率就得重切模板(同「跳过剧情」按钮)。
/// </summary>
public sealed class WuWaSkipConfirmDetector : IDisposable
{
    private const string NoticeTemplatePath = @"GameTask\WuWa\tem\skipConfirmNotice_900_572_142x20.png";
    private const string OkTemplatePath = @"GameTask\WuWa\tem\skipConfirmOk_1161_630_49x27.png";

    /// <summary>「本次登录不再提示」命中阈值(CCoeffNormed)。</summary>
    public const double NoticeMatchThreshold = 0.75;

    /// <summary>「确认」命中阈值(CCoeffNormed)。白字黑底,背景杂色更容易撞脸,门槛抬高一点。</summary>
    public const double OkMatchThreshold = 0.85;

    /// <summary>
    /// 搜索带按画面宽高的比例给(居中模态弹窗,比例比绝对坐标耐用)。
    /// </summary>
    private static readonly (double X0, double X1, double Y0, double Y1) NoticeBand = (0.40, 0.60, 0.48, 0.58);

    private static readonly (double X0, double X1, double Y0, double Y1) OkBand = (0.55, 0.70, 0.55, 0.64);

    private Mat? _noticeTemplate;
    private Mat? _okTemplate;

    /// <summary>找「本次登录不再提示」文字。找不到返回 null,bestScore 给出本次最高得分。</summary>
    public WuWaSkipConfirmHit? DetectNotice(Mat frame, out double bestScore)
    {
        var template = GetTemplate(ref _noticeTemplate, NoticeTemplatePath);
        return Detect(frame, template, NoticeBand, NoticeMatchThreshold, out bestScore);
    }

    /// <summary>找「确认」按钮。找不到返回 null,bestScore 给出本次最高得分。</summary>
    public WuWaSkipConfirmHit? DetectOk(Mat frame, out double bestScore)
    {
        var template = GetTemplate(ref _okTemplate, OkTemplatePath);
        return Detect(frame, template, OkBand, OkMatchThreshold, out bestScore);
    }

    private static WuWaSkipConfirmHit? Detect(
        Mat frame,
        Mat? template,
        (double X0, double X1, double Y0, double Y1) band,
        double threshold,
        out double bestScore)
    {
        bestScore = 0;
        if (template is null)
        {
            return null;
        }

        var x = (int)(band.X0 * frame.Width);
        var y = (int)(band.Y0 * frame.Height);
        var w = Math.Min(frame.Width - x, (int)((band.X1 - band.X0) * frame.Width));
        var h = Math.Min(frame.Height - y, (int)((band.Y1 - band.Y0) * frame.Height));
        if (w < template.Width || h < template.Height)
        {
            return null;
        }

        var roi = new CvRect(x, y, w, h);
        using var sub = new Mat(frame, roi);
        var (loc, score) = TemplateMatchHelper.MatchTemplate(sub, template, TemplateMatchModes.CCoeffNormed);
        bestScore = score;
        if (score < threshold)
        {
            return null;
        }

        return new WuWaSkipConfirmHit
        {
            Rect = new CvRect(roi.X + loc.X, roi.Y + loc.Y, template.Width, template.Height),
            Score = score,
        };
    }

    private static Mat? GetTemplate(ref Mat? cache, string path)
    {
        if (cache != null)
        {
            return cache;
        }

        try
        {
            var fullPath = Global.Absolute(path);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var mat = new Mat(fullPath, ImreadModes.Color);
            if (mat.Empty())
            {
                mat.Dispose();
                return null;
            }

            cache = mat;
            return mat;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _noticeTemplate?.Dispose();
        _noticeTemplate = null;
        _okTemplate?.Dispose();
        _okTemplate = null;
    }
}
