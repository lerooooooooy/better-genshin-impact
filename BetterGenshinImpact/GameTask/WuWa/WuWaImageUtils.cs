using System;
using BetterGenshinImpact.GameTask.Trigger;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.WuWa;

/// <summary>
/// 鸣潮剧情过场动画左上角「跳过」按钮的检测结果。
/// </summary>
public sealed class WuWaSkipButton
{
    /// <summary>
    /// 按钮外接矩形(绝对坐标,capture 坐标系)
    /// </summary>
    public CvRect Rect { get; init; }

    /// <summary>
    /// 按钮中心点(绝对坐标,capture 坐标系)
    /// </summary>
    public Point Center { get; init; }

    /// <summary>
    /// 多边形近似得到的顶点数(5 / 6 / 7)
    /// </summary>
    public int Vertices { get; init; }

    /// <summary>
    /// 轮廓面积 / 包围盒面积
    /// </summary>
    public double Extent { get; init; }

    /// <summary>
    /// 轮廓面积 / 凸包面积
    /// </summary>
    public double Solidity { get; init; }

    /// <summary>
    /// 凸包面积 / 凸包包围盒面积(正六边形 ≈ 0.75)
    /// </summary>
    public double HullRatio { get; init; }

    /// <summary>
    /// 轮廓边沿的平均梯度幅值(Sobel)。硬边 UI 图标几百,柔光光斑几十。
    /// </summary>
    public double EdgeGradient { get; init; }

    /// <summary>
    /// 中心区域最小灰度 / 最大灰度。真按钮中心有暗色 ▶▶,比值 ≈0.3;光斑中心均匀,比值 ≈0.96。
    /// 空心描边渲染时本判据不适用,记为 -1。
    /// </summary>
    public double CenterContrast { get; init; }

    /// <summary>
    /// 凸包左边界竖直平边的长度(像素)。真按钮左右两侧都有较长的竖直平边。
    /// </summary>
    public int FlatSideLeft { get; init; }

    /// <summary>
    /// 凸包右边界竖直平边的长度(像素)。
    /// </summary>
    public int FlatSideRight { get; init; }
}

/// <summary>
/// 鸣潮子系统的图像识别辅助。
/// </summary>
public static class WuWaImageUtils
{
    /// <summary>
    /// 所有像素阈值的基准分辨率高度;实际帧高不同则按比例缩放。
    /// </summary>
    public const int ReferenceHeight = 1080;

    /// <summary>
    /// 1080p 下「跳过」按钮所在的搜索区域(画面左上角)。
    ///
    /// 按钮位置随场景变:过场动画里在 y≈153..206,角色对话界面里会顶到 y≈37..93。
    /// 两种位置都要覆盖,所以搜索区域从画面顶端开始取。
    /// </summary>
    public static readonly CvRect SkipButtonRoi = new(0, 0, 240, 280);

    // ---- 检测参数(1080p 基准)----
    private const int TopHatKernel = 15;      // 顶帽结构元:需大于描边宽度、小于按钮尺寸
    private const int BlurKernel = 3;         // 高斯模糊,压掉压缩噪点
    private const int CloseKernel = 3;        // 闭运算,连接描边断点
    private const int ResponseThreshold = 18; // 顶帽响应阈值 = 描边与局部背景的亮度差
    private const int MinContourArea = 200;   // 轮廓面积下界(1080p 像素)
    private const int MinBoxSide = 24;        // 包围盒边长下界(1080p 像素)
    private const int MaxBoxSide = 64;        // 包围盒边长上界(1080p 像素)
    private const double MinAspect = 0.55;    // 宽 / 高
    private const double MaxAspect = 1.45;
    private const double MinExtent = 0.45;    // 轮廓面积 / 包围盒面积
    private const double MaxExtent = 0.90;
    private const double MinSolidity = 0.78;  // 轮廓面积 / 凸包面积
    private const double MinHullRatio = 0.60; // 凸包面积 / 凸包包围盒面积(正六边形 ≈ 0.75)
    private const double MaxHullRatio = 0.85;

    /// <summary>
    /// 轮廓边沿的平均梯度下界(Sobel 幅值)。
    ///
    /// 跳过按钮是硬边 UI 图标,边缘 1~2 像素内亮度从背景跳到白色;
    /// 游戏里的发光粒子(光斑)边缘是平滑衰减的,每像素只差几个灰度级,
    /// 轮廓形状还凑巧接近六边形,靠顶点/面积这些几何量挡不住。
    ///
    /// **这个量跟背景亮度有关,不能按深色背景上的样本定阈值**(2026-11-08 修复「还是无法识别」):
    /// 按钮是白色的,背景越亮,边缘的亮度落差越小,梯度就越低。深色场景下实测 472~633,
    /// 浅灰背景(过场动画的散焦背景)下只有 225~232 —— 同一个按钮,梯度差一倍以上。
    /// 阈值按 250 定,浅色背景时整帧全灭,日志表现为「连续 N 帧未识别到」。
    ///
    /// 实测分布:真按钮最低 225(浅色背景)/ 472(深色背景)/ 633(对话界面);
    /// 干扰物一侧:柔光光斑 22、其它噪点 33~69。阈值取 150,两侧各留 1.5~2 倍余量。
    /// </summary>
    private const double MinEdgeGradient = 150;

    /// <summary>
    /// 中心区域里「暗 / 亮」灰度比的上限。
    ///
    /// 真按钮中间挖了个深色的 ▶▶ 图标 → 中心区域同时有暗(图标)和亮(亮块)两种像素,
    /// 最小/最大灰度比很小(实测 0.28~0.31)。
    /// 左上角那种柔光光斑是平滑亮团,中心是亮度峰值、处处等亮 → 比值接近 1(实测 0.96)。
    /// 这个比值与光斑大小无关(中心永远是峰值),是最稳的一条判据。
    /// </summary>
    private const double MaxCenterContrast = 0.75;

    /// <summary>
    /// 中心区域半径占包围盒边长的比例。▶▶ 图标就在正中间,取 0.18 刚好罩住图标。
    /// </summary>
    private const double CenterRegionRatio = 0.18;

    /// <summary>
    /// 左右竖直平边长度占包围盒高度的比例下界。
    ///
    /// 真按钮是「尖顶尖底 + 左右两侧竖直平边」的六边形,实测平边占高度 0.38(37x53)/ 0.48(49x56)。
    /// 画面左上角那个误检物是个「左尖右方」的箭头加一块亮面板:右边有 30px 的竖直边,
    /// 左边只有 2px(尖角),所以这条判据能把两者分开。
    /// </summary>
    private const double MinFlatSideRatio = 0.15;

    /// <summary>
    /// 左右竖直平边长度差的容忍度(占包围盒高度)。真按钮两边基本等长(实测差 2~3px)。
    /// </summary>
    private const double MaxFlatSideDiffRatio = 0.25;

    /// <summary>
    /// 中心区域里「有结构」的最低覆盖率。
    ///
    /// 按钮有两种渲染:实心(亮块挖掉 ▶▶)和空心描边(细亮线围一圈,中间是背景)。
    /// 空心渲染时中心区域几乎没有顶帽响应(覆盖率 ≈ 0),此时的判据是「空心环」而不是「暗图标」,
    /// 所以覆盖率太低就跳过本条判据,交给几何判据处理。
    /// </summary>
    private const double MinCenterCoverage = 0.15;

    /// <summary>
    /// approxPolyDP 的 eps 扫描系数(乘以周长)。取第一个落在 {5,6,7} 的结果。
    ///
    /// 上限必须卡在 0.03:圆形/椭圆这类干扰形状在细 eps 下是 8+ 个顶点,
    /// 只有 eps ≥ 0.04 时才会塌缩成 5~7 个而被误当成六边形。
    /// 实机左上角的任务进度徽章(金色圆环,顶帽响应后是个 25x33 的实心圆)
    /// 就是这样被误判成「跳过」按钮的。
    /// </summary>
    private static readonly double[] EpsCandidates = [0.02, 0.025, 0.03];

    /// <summary>
    /// 按帧高度推导缩放系数(1080p = 1.0)。
    /// </summary>
    public static double GetScale(int frameHeight) => frameHeight / (double)ReferenceHeight;

    /// <summary>
    /// 按帧高度推导「跳过」按钮的搜索区域(UI 缩放跟随帧高,故按高度等比缩放)。
    /// </summary>
    public static CvRect GetSkipButtonRoi(int frameHeight)
    {
        var s = GetScale(frameHeight);
        return new CvRect(
            (int)Math.Round(SkipButtonRoi.X * s),
            (int)Math.Round(SkipButtonRoi.Y * s),
            (int)Math.Round(SkipButtonRoi.Width * s),
            (int)Math.Round(SkipButtonRoi.Height * s));
    }

    /// <summary>
    /// 在 <paramref name="roi"/> 内检测「跳过」按钮。
    ///
    /// 该按钮是一个**六边形 + 内部 ▶▶ 图标**,六边形有两种渲染:
    /// 空心描边(细亮线)和实心填充(亮块中间挖掉 ▶▶)。两者像素分布都极不均匀,
    /// 模板匹配(CCoeffNormed)得分很低,所以这里不用模板。
    ///
    /// 算法(形态学顶帽 + 多边形几何):
    /// 1. 灰度 → 高斯模糊 → **顶帽**(gray - 开运算(gray))。顶帽响应的是"比周围背景亮的细结构",
    ///    与背景明暗无关,所以亮墙 / 天空 / 夜景都能检出;直接卡亮度阈值在亮背景上会全灭。
    /// 2. 顶帽响应二值化 → 闭运算连接描边断点 → 取外轮廓。
    /// 3. 几何过滤:**凸包**是唯一稳定的形状描述 —— 描边断裂或内部图标会让原始外轮廓缺角变形,
    ///    但凸包始终是完整的六边形。所以尺寸、宽高比、面积占比、顶点数全部基于凸包计算,
    ///    原始轮廓只用来算 solidity(轮廓面积/凸包面积),防止把不相关的团块也当成六边形。
    ///    顶点数用 approxPolyDP 在凸包上扫描,正六边形 = 6;圆形等干扰形状会得到 4 或 8+,被挡掉。
    ///    光看顶点数不够 —— 画面左上角那个箭头状 UI 也能凑出 6 个顶点,所以还要看**朝向**:
    ///    真按钮是尖顶尖底、左右两侧竖直平边的六边形,左右平边长度都要够长且互相接近。
    /// 4. 命中多个时取最靠左的那个(跳过按钮固定在左上角)。
    ///
    /// </summary>
    /// <param name="image">BGR 图</param>
    /// <param name="roi">搜索区域(内部会 ClampRoi,允许越界)</param>
    /// <param name="scale">分辨率缩放系数,通常传 <see cref="GetScale"/>(帧高) 的结果</param>
    /// <returns>命中返回结果,未命中返回 null</returns>
    public static WuWaSkipButton? DetectSkipButton(Mat image, CvRect roi, double scale = 1.0)
    {
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var safeRoi = ImageUtils.ClampRoi(roi, image.Width, image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            return null;
        }

        using var sub = new Mat(image, safeRoi);
        using var gray = new Mat();
        Cv2.CvtColor(sub, gray, ColorConversionCodes.BGR2GRAY);

        var blur = OddAtLeast(BlurKernel, scale);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(blur, blur), 0);

        var topHatSize = OddAtLeast(TopHatKernel, scale);
        using var topHatKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(topHatSize, topHatSize));
        using var topHat = new Mat();
        Cv2.MorphologyEx(blurred, topHat, MorphTypes.TopHat, topHatKernel);

        using var mask = new Mat();
        // OpenCV 的 Binary 阈值是 "src > thresh",减 0.5 等价于 "src >= ResponseThreshold"
        Cv2.Threshold(topHat, mask, ResponseThreshold - 0.5, 255, ThresholdTypes.Binary);

        var close = OddAtLeast(CloseKernel, scale);
        if (close > 1)
        {
            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(close, close));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        }

        Cv2.FindContours(mask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        using var edgeMagnitude = ComputeEdgeMagnitude(gray);

        var minArea = MinContourArea * scale * scale;
        var minSide = MinBoxSide * scale;
        var maxSide = MaxBoxSide * scale;

        WuWaSkipButton? best = null;
        foreach (var contour in contours)
        {
            if (contour.Length < 3)
            {
                continue;
            }

            var area = Cv2.ContourArea(contour);
            if (area < minArea)
            {
                continue;
            }

            var hull = Cv2.ConvexHull(contour);
            var hullArea = Cv2.ContourArea(hull);
            if (hullArea <= 0)
            {
                continue;
            }

            var box = Cv2.BoundingRect(hull);
            if (box.Width < minSide || box.Width > maxSide ||
                box.Height < minSide || box.Height > maxSide)
            {
                continue;
            }

            var aspect = box.Width / (double)box.Height;
            if (aspect < MinAspect || aspect > MaxAspect)
            {
                continue;
            }

            var hullRatio = hullArea / (box.Width * (double)box.Height);
            if (hullRatio < MinHullRatio || hullRatio > MaxHullRatio)
            {
                continue;
            }

            var solidity = area / hullArea;
            if (solidity < MinSolidity)
            {
                continue;
            }

            var extent = area / (box.Width * (double)box.Height);
            if (extent < MinExtent || extent > MaxExtent)
            {
                continue;
            }

            var vertices = CountVertices(hull);
            if (vertices is not (5 or 6 or 7))
            {
                continue;
            }

            // 六边形朝向检查:真按钮是尖顶尖底、左右两侧竖直平边的六边形。
            // 顶点个数相同不代表形状相同,画面左上角那个箭头状 UI 也能凑出 6 个顶点。
            var flatLeft = MeasureFlatSideLength(hull, box, left: true);
            var flatRight = MeasureFlatSideLength(hull, box, left: false);
            if (!HasFlatVerticalSides(flatLeft, flatRight, box))
            {
                continue;
            }

            // 硬边检查:柔光光斑的边缘梯度只有几十,UI 图标的几百。
            var gradient = MeanEdgeGradient(edgeMagnitude, contour);
            if (gradient < MinEdgeGradient)
            {
                continue;
            }

            var moments = Cv2.Moments(contour);
            if (moments.M00 <= 0)
            {
                continue;
            }

            // 中心结构检查:真按钮中心有深色 ▶▶(暗亮并存),光斑中心是均匀的亮度峰值。
            var centerContrast = MeasureCenterContrast(gray, mask, contour, box);
            if (centerContrast >= 0 && centerContrast > MaxCenterContrast)
            {
                continue;
            }

            var hit = new WuWaSkipButton
            {
                Rect = new CvRect(safeRoi.X + box.X, safeRoi.Y + box.Y, box.Width, box.Height),
                Center = new Point(
                    safeRoi.X + (int)Math.Round(moments.M10 / moments.M00),
                    safeRoi.Y + (int)Math.Round(moments.M01 / moments.M00)),
                Vertices = vertices,
                Extent = extent,
                Solidity = solidity,
                HullRatio = hullRatio,
                EdgeGradient = gradient,
                CenterContrast = centerContrast,
                FlatSideLeft = flatLeft,
                FlatSideRight = flatRight,
            };

            if (best is null || hit.Center.X < best.Center.X)
            {
                best = hit;
            }
        }

        return best;
    }

    /// <summary>
    /// 在整帧左上角按默认参数检测「跳过」按钮。
    /// </summary>
    public static WuWaSkipButton? DetectSkipButton(Mat frame)
    {
        var roi = GetSkipButtonRoi(frame.Height);
        return DetectSkipButton(frame, roi, GetScale(frame.Height));
    }

    /// <summary>
    /// 测量轮廓中心区域的「暗亮并存」程度。
    ///
    /// 返回 中心区域最小灰度 / 最大灰度;中心区域没有顶帽响应(空心描边渲染)时返回 -1,表示本条判据不适用。
    /// </summary>
    private static double MeasureCenterContrast(Mat gray, Mat mask, Point[] contour, CvRect box)
    {
        using var fill = new Mat(gray.Size(), MatType.CV_8U, Scalar.All(0));
        Cv2.DrawContours(fill, new[] { contour }, -1, Scalar.All(255), -1);

        var fillMoments = Cv2.Moments(fill);
        if (fillMoments.M00 <= 0)
        {
            return -1;
        }

        var cx = (int)Math.Round(fillMoments.M10 / fillMoments.M00);
        var cy = (int)Math.Round(fillMoments.M01 / fillMoments.M00);

        var radius = Math.Max(2, (int)Math.Round(CenterRegionRatio * Math.Max(box.Width, box.Height)));

        using var centerCircle = new Mat(gray.Size(), MatType.CV_8U, Scalar.All(0));
        Cv2.Circle(centerCircle, new Point(cx, cy), radius, Scalar.All(255), -1);

        using var centerRegion = new Mat();
        Cv2.BitwiseAnd(centerCircle, fill, centerRegion);

        var totalPixels = Cv2.CountNonZero(centerRegion);
        if (totalPixels <= 0)
        {
            return -1;
        }

        using var centerMask = new Mat();
        Cv2.BitwiseAnd(mask, centerRegion, centerMask);
        var coverage = (double)Cv2.CountNonZero(centerMask) / totalPixels;

        // 中心几乎没有顶帽响应 → 空心描边渲染,本条判据不适用。
        if (coverage < MinCenterCoverage)
        {
            return -1;
        }

        Cv2.MinMaxLoc(gray, out var minVal, out var maxVal, out _, out _, centerRegion);
        if (maxVal <= 0)
        {
            return -1;
        }

        return minVal / maxVal;
    }

    /// <summary>
    /// 量出凸包某一侧极值列上的纵坐标跨度,即该侧的竖直平边长度。
    ///
    /// 六边形左右两侧都是竖直的,凸包最左/最右的一列会落在这条平边上;
    /// 取这一列所有顶点的 y 跨度就是平边长度。
    /// </summary>
    private static int MeasureFlatSideLength(Point[] hull, CvRect box, bool left)
    {
        // 平边允许有 1~2 像素的抗锯齿抖动,用宽度的一个百分比做容差。
        var tolX = Math.Max(1, (int)Math.Round(0.06 * box.Width));

        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var p in hull)
        {
            var onSide = left
                ? p.X <= box.X + tolX
                : p.X >= box.X + box.Width - 1 - tolX;

            if (!onSide)
            {
                continue;
            }

            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return maxY <= minY ? 0 : maxY - minY;
    }

    /// <summary>
    /// 凸包是否呈「左右两侧都有足够长、且长度接近的竖直平边」的六边形。
    /// </summary>
    private static bool HasFlatVerticalSides(int flatLeft, int flatRight, CvRect box)
    {
        var minSide = MinFlatSideRatio * box.Height;
        if (flatLeft < minSide || flatRight < minSide)
        {
            return false;
        }

        return Math.Abs(flatLeft - flatRight) <= MaxFlatSideDiffRatio * box.Height;
    }

    /// <summary>
    /// 灰度图的 Sobel 梯度幅值,整块 ROI 算一次复用。
    /// </summary>
    private static Mat ComputeEdgeMagnitude(Mat gray)
    {
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);
        var magnitude = new Mat();
        Cv2.Magnitude(gx, gy, magnitude);
        return magnitude;
    }

    /// <summary>
    /// 轮廓边沿的平均梯度幅值:沿轮廓画 1 像素线,取线上梯度幅值的平均。
    /// </summary>
    private static double MeanEdgeGradient(Mat edgeMagnitude, Point[] contour)
    {
        using var edgeMask = new Mat(edgeMagnitude.Size(), MatType.CV_8U, Scalar.All(0));
        Cv2.DrawContours(edgeMask, new[] { contour }, -1, Scalar.All(255), 1);
        return Cv2.Mean(edgeMagnitude, edgeMask)[0];
    }

    /// <summary>
    /// approxPolyDP 顶点计数:扫描一组 eps,取第一个落在 {5,6,7} 的结果;都不中返回 0。
    /// eps 只在细尺度上扫描(见 <see cref="EpsCandidates"/>),圆形干扰形状因此拿不到假顶点数。
    /// </summary>
    private static int CountVertices(Point[] contour)
    {
        var perimeter = Cv2.ArcLength(contour, true);
        if (perimeter <= 0)
        {
            return 0;
        }

        foreach (var eps in EpsCandidates)
        {
            var approx = Cv2.ApproxPolyDP(contour, eps * perimeter, true);
            if (approx.Length is 5 or 6 or 7)
            {
                return approx.Length;
            }
        }

        return 0;
    }

    /// <summary>
    /// 按缩放系数取奇数尺寸(结构元 / 模糊核都要奇数才能保持中心对称)。
    /// </summary>
    private static int OddAtLeast(int size, double scale)
    {
        var value = (int)Math.Round(size * scale);
        if (value < 1)
        {
            value = 1;
        }

        return value % 2 == 0 ? value + 1 : value;
    }
}
