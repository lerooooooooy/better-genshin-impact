using BetterGenshinImpact.GameTask.WuWa;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.WuWaTests;

public class WuWaSkipButtonTests
{
    [Fact]
    public void DetectSkipButton_ShouldFindButtonInSample()
    {
        using var mat = WuWaTestSample.LoadSkipButton();

        var hit = WuWaImageUtils.DetectSkipButton(mat, new Rect(0, 0, mat.Width, mat.Height));

        Assert.NotNull(hit);
        AssertClose(WuWaTestSample.ExpectedX, hit.Center.X);
        AssertClose(WuWaTestSample.ExpectedY, hit.Center.Y);
        Assert.Equal(6, hit.Vertices);
    }

    [Fact]
    public void DetectSkipButton_ShouldSurviveUpscale()
    {
        using var mat = WuWaTestSample.LoadSkipButton();
        using var big = new Mat();
        Cv2.Resize(mat, big, new Size(mat.Width * 3 / 2, mat.Height * 3 / 2), 0, 0, InterpolationFlags.Linear);

        var hit = WuWaImageUtils.DetectSkipButton(big, new Rect(0, 0, big.Width, big.Height), scale: 1.5);

        Assert.NotNull(hit);
        AssertClose(WuWaTestSample.ExpectedX * 1.5, hit.Center.X);
        AssertClose(WuWaTestSample.ExpectedY * 1.5, hit.Center.Y);
    }

    [Fact]
    public void DetectSkipButton_ShouldNotFireOnBlankFrame()
    {
        using var blank = new Mat(180, 240, MatType.CV_8UC3, Scalar.All(40));

        Assert.Null(WuWaImageUtils.DetectSkipButton(blank, new Rect(0, 0, blank.Width, blank.Height)));
    }

    /// <summary>
    /// 实机会把按钮渲染成**实心六边形**(亮块 + 中间挖掉 ▶▶),
    /// 此时原始外轮廓会缺角变形,只有凸包仍是完整六边形。
    /// </summary>
    [Fact]
    public void DetectSkipButton_ShouldFindFilledHexagonInLiveSample()
    {
        using var mat = WuWaTestSample.LoadFilledSkipButton();

        var hit = WuWaImageUtils.DetectSkipButton(mat, new Rect(0, 0, mat.Width, mat.Height));

        Assert.NotNull(hit);
        AssertClose(77.5, hit.Center.X, 3.0);
        AssertClose(83.0, hit.Center.Y, 3.0);
        Assert.Equal(6, hit.Vertices);
    }

    [Fact]
    public void DetectSkipButton_ShouldNotFireOnCircle()
    {
        using var img = new Mat(180, 240, MatType.CV_8UC3, Scalar.All(40));
        Cv2.Circle(img, new Point(80, 90), 20, Scalar.White, 2);

        Assert.Null(WuWaImageUtils.DetectSkipButton(img, new Rect(0, 0, img.Width, img.Height)));
    }

    /// <summary>
    /// 角色对话界面里按钮顶到画面左上角(实机位置 y≈37..93),
    /// 旧的搜索区域从 y=100 起,整帧都搜不到它。
    /// </summary>
    [Fact]
    public void DetectSkipButton_ShouldFindButtonInDialogScene()
    {
        using var mat = WuWaTestSample.LoadDialogSkipButton();

        var hit = WuWaImageUtils.DetectSkipButton(mat, new Rect(0, 0, mat.Width, mat.Height));

        Assert.NotNull(hit);
        AssertClose(105.0, hit.Center.X, 3.0);
        AssertClose(64.0, hit.Center.Y, 3.0);
        Assert.Equal(6, hit.Vertices);
    }

    /// <summary>
    /// 画面左上角的金色圆形徽章不是「跳过」按钮。
    /// 旧的顶点扫描把 eps 放宽到 0.06,圆形在粗 eps 下会塌缩成 7 个顶点而被误判,
    /// 导致在地图界面反复空投 ESC。
    /// </summary>
    [Fact]
    public void DetectSkipButton_ShouldNotFireOnRoundBadge()
    {
        using var mat = WuWaTestSample.LoadRoundBadge();

        Assert.Null(WuWaImageUtils.DetectSkipButton(mat, new Rect(0, 0, mat.Width, mat.Height)));
    }

    /// <summary>
    /// 默认搜索区域必须覆盖到画面顶端,否则对话界面里的按钮搜不到。
    /// </summary>
    [Fact]
    public void SkipButtonRoi_ShouldStartAtTopOfFrame()
    {
        var roi = WuWaImageUtils.GetSkipButtonRoi(1080);

        Assert.Equal(0, roi.Y);
        Assert.True(roi.Height >= 280, $"搜索区域高度 {roi.Height} 不足以覆盖对话界面的按钮位置");
    }

    private static void AssertClose(double expected, double actual, double tolerance = 2.0)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"期望 {expected:F1},实际 {actual:F1},容差 {tolerance:F1}");
    }
}
