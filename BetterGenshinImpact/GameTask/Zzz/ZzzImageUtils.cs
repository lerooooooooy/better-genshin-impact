using System;
using System.IO;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// ZZZ 子系统共享的图像处理原语。
/// 集中 ROI 边界钳制 + HSV 颜色掩膜 + 连通域形状过滤;被 Daily/Test 两个 trigger 共用。
/// </summary>
public static class ZzzImageUtils
{
    /// <summary>
    /// 把可能越界的 Roi 钳制到当前帧 [0, W) × [0, H);完全越界退回整个图。
    /// </summary>
    public static CvRect ClampRoi(CvRect roi, int width, int height)
    {
        var x1 = Math.Max(0, roi.X);
        var y1 = Math.Max(0, roi.Y);
        var x2 = Math.Min(width, roi.X + roi.Width);
        var y2 = Math.Min(height, roi.Y + roi.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return new CvRect(0, 0, width, height);
        }

        return new CvRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// 在 ROI 内做 HSV 颜色掩膜 + 连通域形状过滤,判定是否存在"箭头形"白色亮斑。
    ///
    /// 七道防线(由廉到贵):
    /// - **连通域数量** ≤ maxLabels:箭头 »» 是单连通域(2 个 chevron 在 8-connectivity 下粘合);
    ///   进度条 / 任务追踪条是"主条 + 装饰突起"的多 blob 结构(3~8 个连通域)
    /// - **连通域面积** [minArea, maxArea]:过滤掉 1-pixel 反走样噪声 / 整片 UI 高光
    /// - **包围盒高度** ≥ minHeight:箭头 bbox 约 14×20(高 > 宽);进度条 bbox 约 30×12(宽 >> 高)
    /// - **包围盒宽高比** ≥ minAspect:箭头 »» 是纵向条状,UI 字符接近 1:1、横向文字接近 1:N
    /// - **extent** ≥ minExtent (面积 / 包围盒面积):箭头是密集斑块,稀疏拉丝噪声 extent 接近 0
    /// - **亮像素集中度** ≥ minLargestFraction (最大连通域 / 总亮像素数):箭头是单一斑块,
    ///   UI 散点群(F/J/Q 按钮图标 + 任务标记 + 杂散亮边)的最大连通域通常只占总亮像素的 10~20%,
    ///   而箭头通常占 90%+ —— 这是区分"成片箭头"和"散落 UI"的核心判据
    /// - **凸性** ≤ maxConvexity (轮廓面积 / 凸包面积):箭头 »» 是**凹形**(chevron 内部有 V 形凹陷),
    ///   UI 按钮图标 / 字符是**凸形**。这是真正区分"形状"的判据,而前几道只是包围盒统计量。
    ///
    /// 用于继续对话箭头识别。完全越界或 ROI 为空返回 false。
    /// </summary>
    /// <param name="image">BGR 图</param>
    /// <param name="roi">检测区域(本方法内部会 ClampRoi,允许越界)</param>
    /// <param name="sMax">HSV 饱和度上界(箭头白灰 S≈0 远低于此)</param>
    /// <param name="vMin">HSV 亮度下界(箭头亮白 V≈150+ 高于此)</param>
    /// <param name="minArea">最大连通域最小像素数</param>
    /// <param name="maxArea">最大连通域最大像素数</param>
    /// <param name="minHeight">最大连通域包围盒高度下界 — »» ≈ 20 px;进度条 ≈ 12 px</param>
    /// <param name="minAspect">宽高比下界 = max(w,h) / min(w,h)</param>
    /// <param name="minExtent">密集度下界 = area / (w * h)</param>
    /// <param name="minLargestFraction">亮像素集中度下界 = 最大连通域面积 / 总亮像素数</param>
    /// <param name="maxConvexity">凸性上界 = 轮廓面积 / 凸包面积。»» 通常 0.6~0.8(凹);UI 按钮通常 0.9+(凸)</param>
    /// <param name="maxLabels">连通域总数上界 — »» 通常 1(动画极端相位 ≤ 2);进度条 3~8</param>
    public static bool DetectArrowBlob(
        Mat image,
        CvRect roi,
        int sMax,
        int vMin,
        int minArea,
        int maxArea,
        int minHeight,
        double minAspect,
        double minExtent,
        double minLargestFraction,
        double maxConvexity,
        int maxLabels)
    {
        var safeRoi = ClampRoi(roi, image.Width, image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            return false;
        }

        using var sub = new Mat(image, safeRoi);
        using var hsv = new Mat();
        Cv2.CvtColor(sub, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(0, 0, vMin),
            new Scalar(179, sMax, 255),
            mask);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var nLabels = Cv2.ConnectedComponentsWithStats(
            mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity8, ltype: MatType.CV_32S);

        if (nLabels <= 1)
        {
            return false; // 只有背景
        }

        // 连通域数量过滤:箭头 »» 通常 1 个连通域(动画极端相位最多 2 个);
        // 进度条 / 任务追踪条等 UI 元素通常是"主条 + 装饰突起"结构,3~8 个连通域。
        // 这一道在前 — 廉价,先排除明显非箭头形态再算凸性(凸性 ~0.1 ms)。
        if (nLabels - 1 > maxLabels)
        {
            return false;
        }

        // 找面积最大且在阈值内的连通域(跳过 label 0 = 背景)
        int bestLabel = -1;
        int bestArea = 0;
        for (int i = 1; i < nLabels; i++)
        {
            using var row = stats.Row(i);
            if (!row.GetArray(out int[] arr))
            {
                continue;
            }

            var area = arr[4]; // CC_STAT_AREA
            if (area > bestArea)
            {
                bestArea = area;
                bestLabel = i;
            }
        }

        if (bestLabel < 1 || bestArea < minArea || bestArea > maxArea)
        {
            return false;
        }

        using var bestRow = stats.Row(bestLabel);
        if (!bestRow.GetArray(out int[] box))
        {
            return false;
        }

        // box[]: [x, y, width, height, area, ...] —— CC_STAT_LEFT/TOP/WIDTH/HEIGHT/AREA
        var bw = box[2];
        var bh = box[3];
        if (bw <= 0 || bh <= 0)
        {
            return false;
        }

        // 包围盒高度过滤:箭头 bbox 约 14×20(高 20 > 宽 14);
        // 进度条 / 滚动指示器 bbox 约 30×12(宽 >> 高,height ≈ 12 < 16)。
        // 这是"纵向箭头"vs"横向条状 UI"的关键区分。
        if (bh < minHeight)
        {
            return false;
        }

        var aspect = Math.Max(bw, bh) / (double)Math.Min(bw, bh);
        if (aspect < minAspect)
        {
            return false;
        }

        var extent = bestArea / (double)(bw * bh);
        if (extent < minExtent)
        {
            return false;
        }

        // 亮像素集中度:箭头 »» 是单连通域,亮像素几乎全在最大 blob 里(~100%);
        // UI 散点群(底部 F/J/Q 按钮 + 任务标记 + 杂散亮边)有十几二十个连通域,
        // 最大 blob 通常只占 10~20%。这个判据是抗"散落 UI"误识别的核心。
        var totalBright = Cv2.CountNonZero(mask);
        var largestFraction = totalBright > 0 ? bestArea / (double)totalBright : 0;
        if (largestFraction < minLargestFraction)
        {
            return false;
        }

        // 凸性分析:提取最大 blob 单独成 mask → 找轮廓 → 算 convexHull。
        // »» 的两个 chevron 围出 V 形凹陷(凹),convexity ≈ 0.6~0.8;
        // UI 按钮圆形/方形(凸),convexity > 0.9。前 4 道只算包围盒统计量,
        // 这是第一道真正区分"形状"的判据。
        using var blobMask = new Mat();
        Cv2.Compare(labels, new Scalar(bestLabel), blobMask, CmpType.EQ);
        Cv2.FindContours(
            blobMask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return false;
        }
        var contour = contours[0];
        var contourArea = Cv2.ContourArea(contour);
        var hull = Cv2.ConvexHull(contour);
        var hullArea = Cv2.ContourArea(hull);
        if (contourArea <= 0 || hullArea <= 0)
        {
            return false;
        }
        var convexity = contourArea / hullArea;
        if (convexity > maxConvexity)
        {
            return false; // 太凸 → 圆形/方形 UI 元素
        }

        return true;
    }

    /// <summary>
    /// 把 ROI 区域(钳制到帧内)保存为 PNG,落盘到 C:\Users\zz\Desktop\zzz_arrow_hits\。
    /// 文件名 yyyyMMdd_HHmmss_fff_{label}.png,毫秒级时间戳避免覆盖。
    /// 用于离线调试命中 / 误识别场景:触发器命中时保存实际游戏帧 ROI 切片,事后人眼对比 / 给 Claude 验证。
    /// 完全越界抛 ArgumentException。
    /// </summary>
    public static string SaveArrowRoiSnapshot(Mat image, CvRect roi, string label)
    {
        var safeRoi = ClampRoi(roi, image.Width, image.Height);
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
