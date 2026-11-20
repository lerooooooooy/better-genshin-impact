using System.IO;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.WuWaTests;

/// <summary>
/// 鸣潮测试样本加载:1920x1080 原图左上角 (0,100)-(240,280) 的裁剪,
/// 里面是剧情过场动画左上角的「跳过」按钮。
/// </summary>
internal static class WuWaTestSample
{
    /// <summary>
    /// 按钮中心在原图中的绝对坐标约为 (78, 184),换算到样本内为 (78, 84)。
    /// </summary>
    public const int ExpectedX = 78;
    public const int ExpectedY = 84;

    public static Mat LoadSkipButton()
    {
        var path = FindAsset("skip_button_1080p.png");
        Assert.NotNull(path);
        return new Mat(path);
    }

    /// <summary>
    /// 实心填充渲染的「跳过」按钮样本(实机抓帧裁剪),
    /// 按钮中心在样本内约为 (77, 83)。
    /// </summary>
    public static Mat LoadFilledSkipButton()
    {
        var path = FindResource("skip_button_filled_1080p.png");
        Assert.NotNull(path);
        return new Mat(path);
    }

    /// <summary>
    /// 角色对话界面里的「跳过」按钮样本(实机抓帧裁剪)。
    /// 这种场景下按钮会顶到画面左上角(样本内中心约为 (105, 64)),
    /// 位置比过场动画里的更靠上,搜索区域必须覆盖到 y=0。
    /// </summary>
    public static Mat LoadDialogSkipButton()
    {
        var path = FindResource("skip_button_dialog_1080p.png");
        Assert.NotNull(path);
        return new Mat(path);
    }

    /// <summary>
    /// 「剧情快览」界面(含「跳过剧情」按钮)的抓帧裁剪。
    /// 裁剪自 1920x1080 原图的 (1143, 648)-(1457, 875),
    /// 按钮文字在裁剪图内的绝对位置约为 (1243, 748)。
    /// </summary>
    public static Mat LoadSkipStoryScene()
    {
        var path = FindResource("skip_story_dialog_1080p.png");
        Assert.NotNull(path);
        return new Mat(path);
    }

    /// <summary>
    /// 画面左上角 HUD 里带金色圆环的任务进度徽章。
    /// 顶帽响应后它是个 25x33 的实心圆,旧版顶点扫描(eps 放宽到 0.06)
    /// 会把它塌缩成 7 个顶点,误判成「跳过」按钮并空投 ESC。
    /// </summary>
    public static Mat LoadRoundBadge()
    {
        var path = FindResource("skip_button_round_badge_1080p.png");
        Assert.NotNull(path);
        return new Mat(path);
    }

    private static string? FindResource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "GameTaskTests", "WuWaTests", "Resources", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 从程序集所在目录向上找 Assets\WuWa 目录,避免依赖固定的相对层级
    /// (build 时用 -p:OutDir= 重定向输出后相对层级会失效)。
    /// </summary>
    private static string? FindAsset(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "WuWa", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
