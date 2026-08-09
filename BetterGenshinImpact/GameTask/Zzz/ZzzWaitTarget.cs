using System;
using System.Diagnostics;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 多目标 "等待出现" 的目标抽象。一个 target 代表一种"命中条件":
/// 模板图走 <see cref="TemplateWaitTarget"/>,HSV / 形状 / 任意 detector 走 <see cref="HsvWaitTarget"/>。
///
/// 由 <see cref="TestZzzTaskTrigger.WaitForHit"/> 在共享轮询预算内遍历,任一命中即返回。
/// </summary>
public interface IZzzWaitTarget
{
    /// <summary>
    /// 命中后在 caller 处显示的名字(用于 Debug / LogMiss / Advance)。
    /// </summary>
    string Label { get; }

    /// <summary>
    /// 模板匹配阈值(score ≥ threshold 命中)。给日志用,让 caller 知道 score 离阈值差多远。
    /// 模板 target 返回构造时的 _threshold;HSV detector 没有阈值概念,返回 null。
    /// </summary>
    double? Threshold { get; }

    /// <summary>
    /// 在给定 capture 帧上尝试匹配。
    /// 命中返回 true。
    /// 模板命中时 <paramref name="templateResult"/> 非 null(给画框 / 点击坐标用);
    /// HSV 命中时为 null(caller 走 HSV 专属画框路径)。
    /// 模板加载失败 / 匹配失败 → 返回 false + null。
    /// </summary>
    bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? templateResult);
}

/// <summary>
/// 模板图 target:首次 TryMatch 时懒加载 <see cref="TemplateImage"/>,之后复用。
/// 模板加载失败会持续返回 false 直到本次调用结束(不抛异常)。
/// </summary>
public sealed class TemplateWaitTarget : IZzzWaitTarget, IDisposable
{
    public string Label { get; }
    public double? Threshold => _threshold;
    private readonly string _templatePath;
    private readonly double _threshold;
    private TemplateImage? _template;

    public TemplateWaitTarget(string label, string templatePath, double threshold)
    {
        Label = label;
        _templatePath = templatePath;
        _threshold = threshold;
    }

    public bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? templateResult)
    {
        templateResult = null;
        if (_template == null)
        {
            try
            {
                _template = TemplateImage.FromFile(_templatePath, _threshold);
                //Debug.WriteLine($"[ZZZ-Template] loaded name={_template.Name} roi=({_template.Roi.X},{_template.Roi.Y},{_template.Roi.Width},{_template.Roi.Height}) padding={_template.Padding} threshold={_template.Threshold:F2}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Template] {Label} load failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        var result = _template.TryMatch(content);
        templateResult = result;
        return result.Score >= _template.Threshold;
    }

    public void Dispose()
    {
        _template?.Template.Dispose();
        _template = null;
    }
}

/// <summary>
/// HSV / 任意 detector target:把 detector 委托给 caller,TryMatch 只调一次。
/// detector 抛异常会被当作 miss(返回 false),避免单次抛错打挂整个轮询循环。
/// </summary>
public sealed class HsvWaitTarget : IZzzWaitTarget
{
    public string Label { get; }
    public double? Threshold => null;
    private readonly Func<ZzzCaptureContent, bool> _detector;

    public HsvWaitTarget(string label, Func<ZzzCaptureContent, bool> detector)
    {
        Label = label;
        _detector = detector;
    }

    public bool TryMatch(ZzzCaptureContent content, out TemplateMatchResult? templateResult)
    {
        templateResult = null;
        try
        {
            return _detector(content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Wait] {Label} detector threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}