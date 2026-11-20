using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.GameTask.WuWa;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.WuWaTests;

public class WuWaSkipStoryTemplateTests
{
    /// <summary>
    /// 与 <c>CommonWuWaTaskTrigger.SkipStoryTemplatePath</c> 保持一致。
    /// </summary>
    private const string SkipStoryTemplatePath = "GameTask\\WuWa\\tem\\skipStory_1243_748_114x27_200.png";

    private const double MatchThreshold = 0.96;

    [Fact]
    public void SkipStoryTemplate_ShouldMatchStoryRecapScreen()
    {
        using var mat = WuWaTestSample.LoadSkipStoryScene();
        using var content = new TriggerCaptureContent(mat, 0, 50.0, IntPtr.Zero);
        using var target = new TemplateWaitTarget("SkipStory", SkipStoryTemplatePath, MatchThreshold);

        Assert.True(target.TryMatch(content, out var result));
        Assert.NotNull(result);
        Assert.True(result.Score >= MatchThreshold, $"匹配得分 {result.Score:F3} 未达阈值 {MatchThreshold}");

        // 模板是整块文字,匹配框应盖住「跳过剧情」四个字。
        Assert.Equal(114, result.AbsRect.Width);
        Assert.Equal(27, result.AbsRect.Height);
    }

    /// <summary>
    /// 「剧情快览」界面之外的画面不应误命中。
    /// </summary>
    [Fact]
    public void SkipStoryTemplate_ShouldNotMatchDialogScene()
    {
        using var mat = WuWaTestSample.LoadDialogSkipButton();
        using var content = new TriggerCaptureContent(mat, 0, 50.0, IntPtr.Zero);
        using var target = new TemplateWaitTarget("SkipStory", SkipStoryTemplatePath, MatchThreshold);

        Assert.False(target.TryMatch(content, out var result));
        Assert.True(result is null || result.Score < MatchThreshold);
    }

    /// <summary>
    /// 搜索区域要能容忍按钮整体平移:文案变长时「跳过剧情」四个字会跟着挪位置,
    /// 平移几十像素后仍要能匹配上。
    /// </summary>
    [Fact]
    public void SkipStoryTemplate_ShouldSurvivePositionShift()
    {
        using var crop = WuWaTestSample.LoadSkipStoryScene();
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(0));

        const int shiftX = -90;
        const int shiftY = -90;
        using (var dst = new Mat(frame, new Rect(1143 + shiftX, 648 + shiftY, crop.Width, crop.Height)))
        {
            crop.CopyTo(dst);
        }

        using var content = new TriggerCaptureContent(frame, 0, 50.0, IntPtr.Zero);
        using var target = new TemplateWaitTarget("SkipStory", SkipStoryTemplatePath, MatchThreshold);

        Assert.True(target.TryMatch(content, out var result));
        Assert.NotNull(result);
        Assert.True(result.Score >= MatchThreshold, $"匹配得分 {result.Score:F3} 未达阈值 {MatchThreshold}");
        Assert.Equal(1243 + shiftX, result.AbsRect.X);
        Assert.Equal(748 + shiftY, result.AbsRect.Y);
    }
}
