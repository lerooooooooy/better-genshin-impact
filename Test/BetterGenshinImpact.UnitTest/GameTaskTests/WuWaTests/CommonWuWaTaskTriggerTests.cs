using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.GameTask.WuWa;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.WuWaTests;

public class CommonWuWaTaskTriggerTests
{
    [Fact]
    public void OnCapture_WithButtonPresent_ShouldNotThrow()
    {
        var mat = WuWaTestSample.LoadSkipButton();
        using var content = new TriggerCaptureContent(mat, 0, 50.0, IntPtr.Zero);

        var trigger = new CommonWuWaTaskTrigger(logger: new FakeLogger<CommonWuWaTaskTrigger>());
        trigger.OnCapture(content);
    }

    [Fact]
    public void OnCapture_WithoutButton_ShouldNotThrow()
    {
        using var blank = new Mat(180, 240, MatType.CV_8UC3, Scalar.All(40));
        using var content = new TriggerCaptureContent(blank, 0, 50.0, IntPtr.Zero);

        var trigger = new CommonWuWaTaskTrigger(logger: new FakeLogger<CommonWuWaTaskTrigger>());
        trigger.OnCapture(content);
    }
}
