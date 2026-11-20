using System;
using System.IO;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// ZZZ 子系统专属的图像处理辅助。
/// 通用的 ROI 钳制 / HSV 形状检测在 <see cref="BetterGenshinImpact.GameTask.Trigger.ImageUtils"/>。
/// </summary>
public static class ZzzImageUtils
{
    /// <summary>
    /// 把 ROI 区域(钳制到帧内)保存为 PNG,落盘到 C:\Users\zz\Desktop\zzz_arrow_hits\。
    /// 文件名 yyyyMMdd_HHmmss_fff_{label}.png,毫秒级时间戳避免覆盖。
    /// 用于离线调试命中 / 误识别场景:触发器命中时保存实际游戏帧 ROI 切片,事后人眼对比 / 给 Claude 验证。
    /// 完全越界抛 ArgumentException。
    /// </summary>
    public static string SaveArrowRoiSnapshot(Mat image, CvRect roi, string label)
    {
        var safeRoi = BetterGenshinImpact.GameTask.Trigger.ImageUtils.ClampRoi(roi, image.Width, image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            throw new ArgumentException(
                $"ROI 完全越界: roi=({roi.X},{roi.Y},{roi.Width}x{roi.Height}) frame={image.Width}x{image.Height}");
        }

        const string saveDir = @"C:\Users\zz\Desktop\zzz_arrow_hits\";
        Directory.CreateDirectory(saveDir);

        var filename = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{label}.png";
        var fullPath = Path.Combine(saveDir, filename);

        using var sub = new Mat(image, safeRoi);
        Cv2.ImWrite(fullPath, sub);
        return fullPath;
    }
}
