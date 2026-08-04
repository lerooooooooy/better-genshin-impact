using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OpenCv.TemplateMatch;
using BetterGenshinImpact.Core.Simulator;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace BetterGenshinImpact.GameTask.Zzz;

public sealed class DailyTaskZzzTrigger : IZzzTaskTrigger, IDisposable
{
    private const string TemplateFolder = "Assets\\Template\\Daily";
    private const string SelectedTemplateName = "daliySel_916_145_143x60.png";
    private const string NotSelectedTemplateName = "daliyNoSel_926_158_129x43.png";

    private const string F2TemplateName = "daliyF2_1538_116_36x24.png";
    private static readonly CvRect F2Roi = new(1538, 116, 36, 24);

    private const double MatchSuccessThreshold = 0.99;
    private const int ClickPreDelayMinMs = 400;
    private const int ClickPreDelayMaxMs = 500;
    private const int ClickCooldownMs = 3000;
    private const int KeyPressHoldMs = 50;

    private static readonly CvRect SelectedTabRoi = new(916, 145, 143, 60);
    private static readonly CvRect NotSelectedTabRoi = new(926, 158, 129, 43);
    private static readonly CvRect QianWangRoi = new(842, 820, 243, 89);

    // 继续对话箭头 »» 半透明白灰,实测无彩色(S≈0) + 高亮度(V≈141~184),不能用色相识别
    private static readonly CvRect ContinueArrowRoi = new(1462, 966, 42, 45);
    private const int ContinueArrowSMax = 40;
    private const int ContinueArrowVMin = 130;
    private const int ContinueArrowMinPixels = 120;
    private const int ContinueArrowMaxPixels = 900;

    // 对话点单第一个选项框:文字每次不同,只匹配左侧「①」数字徽标(选项1独有,▶ 箭头选项1/2 都有不唯一)。
    private const string DialogOption1TemplateName = "dialogOpt1_1376_570_44x44.png";
    // ROI 比模板略大,给一点位移余量。覆盖第一个选项左侧「①」徽标区域。
    private static readonly CvRect DialogOption1Roi = new(1366, 560, 66, 64);
    // 命中后要点的位置:第一个选项框中心(整框,不是图标)。
    private static readonly DrawingPoint DialogOption1ClickPoint = new(1630, 588);
    // 徽标较小,半透明背景可能掉分,给独立阈值方便调(比 0.99 宽松)。
    private const double DialogOption1Threshold = 0.85;

    // 跳过按钮「跳过」(右上角),命中后点击跳过。文件名坐标 1633,38,189x44。
    private const string SkipTemplateName = "skip2_1633_38_189x44.png";
    private static readonly CvRect SkipRoi = new(1633, 38, 189, 44);
    private const double SkipThreshold = 0.8;

    // 获得电池奖励弹窗(屏幕中央),命中后按 ESC 关闭。文件名坐标 907,464,105x105。
    private const string BatteryTemplateName = "getBattery_907_464_105x105.png";
    private static readonly CvRect BatteryRoi = new(907, 464, 105, 105);
    private const double BatteryThreshold = 0.8;

    private Mat? _selectedTemplate;
    private Mat? _notSelectedTemplate;
    private Mat? _f2Template;
    private Mat? _dialogOption1Template;
    private Mat? _skipTemplate;
    private Mat? _batteryTemplate;
    private IOcrService? _ocr;
    private long _nextClickTickMs;
    private long _lastClickTickMs;
    private int _lastClickFrame = -1;

    private int _sequence;
    private bool _finished;

    private readonly Action<DrawingRectangle, ZzzCaptureContent, string>? _onMatch;
    private readonly Action<DrawingPoint, ZzzCaptureContent>? _onClick;
    private readonly Action? _onFinished;

    public string Name => "DailyTaskZzzTrigger";
    public bool IsEnabled { get; set; } = true;

    public DailyTaskZzzTrigger(
        Action<DrawingRectangle, ZzzCaptureContent, string>? onMatch = null,
        Action<DrawingPoint, ZzzCaptureContent>? onClick = null,
        Action? onFinished = null)
    {
        _onMatch = onMatch;
        _onClick = onClick;
        _onFinished = onFinished;
    }

    /// <summary>
    /// 序列执行完成或无法执行下去时调用:停用本 trigger 并请求上层关闭 UI 开关。
    /// 一次性,重复调用无副作用。用户下次重新打开开关时 dispatcher 会重建实例从 seq 0 再来。
    /// </summary>
    private void Finish(string reason)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        IsEnabled = false;
        _sequence = 0;
        Debug.WriteLine($"[ZZZ-Daily] FINISH ({reason}) → 停用 trigger + 请求关闭 UI 开关");
        _onFinished?.Invoke();
    }

    private IOcrService Ocr
    {
        get
        {
            if (_ocr != null)
            {
                return _ocr;
            }

            var ocr = OcrFactory.Paddle;
            Debug.WriteLine("[ZZZ-Daily] OCR loaded");
            return _ocr = ocr;
        }
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        var foreground = GetForegroundHandle();
        var sinceLastClick = _lastClickTickMs == 0 ? -1 : GetCurrentTickMs() - _lastClickTickMs;
        var cooldownRemaining = Math.Max(0, _nextClickTickMs - GetCurrentTickMs());
        Debug.WriteLine(
            $"[ZZZ-Daily] OnCapture frame={content.FrameIndex} seq={_sequence} hwnd=0x{content.Hwnd:X} image={content.Image.Width}x{content.Image.Height} foreground=0x{foreground:X} match={foreground == content.Hwnd} sinceLastClick={sinceLastClick}ms cooldownLeft={cooldownRemaining}ms framesSinceLastClick={(_lastClickFrame < 0 ? -1 : content.FrameIndex - _lastClickFrame)}");

        _selectedTemplate ??= LoadTemplate(SelectedTemplateName);
        _notSelectedTemplate ??= LoadTemplate(NotSelectedTemplateName);
        if (_selectedTemplate == null || _notSelectedTemplate == null)
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] skip frame={content.FrameIndex}: template missing sel={_selectedTemplate != null} noSel={_notSelectedTemplate != null}");
            Finish("core template missing");
            return;
        }

        if (IsInCooldown())
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] skip frame={content.FrameIndex}: cooldown left {cooldownRemaining}ms");
            return;
        }

        switch (_sequence)
        {
            case 0:
                ExecuteTabCheck(content);
                break;
            case 1:
                ExecuteQianWangOcr(content);
                break;
            case 2:
                ExecuteF2Match(content);
                break;
            case 3:
                ExecuteContinueArrow(content);
                break;
            case 4:
                ExecuteDialogOption1(content);
                break;
            case 5:
                ExecuteSkip(content);
                break;
            case 6:
                ExecuteBattery(content);
                break;
            default:
                _sequence = 0;
                break;
        }
    }

    private void ExecuteTabCheck(ZzzCaptureContent content)
    {
        var selectedHit = TryMatch(content, _selectedTemplate!, SelectedTabRoi, out var selScore);
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=0 tab selected hit={selectedHit} score={selScore:F3}");

        if (selectedHit)
        {
            _sequence = 1;
            Debug.WriteLine("[ZZZ-Daily] seq=0 → seq=1 (tab already selected)");
            return;
        }

        var notSelectedHit = TryMatch(content, _notSelectedTemplate!, NotSelectedTabRoi, out var noSelScore);
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=0 tab not-selected hit={notSelectedHit} score={noSelScore:F3}");
        if (!notSelectedHit)
        {
            return;
        }

        var tabX = NotSelectedTabRoi.X + NotSelectedTabRoi.Width / 2;
        var tabY = NotSelectedTabRoi.Y + NotSelectedTabRoi.Height / 2;
        _onMatch?.Invoke(
            new DrawingRectangle(NotSelectedTabRoi.X, NotSelectedTabRoi.Y, NotSelectedTabRoi.Width, NotSelectedTabRoi.Height),
            content, "日常 tab 未选中");
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=0 try click tab at ({tabX},{tabY}) foreground=0x{GetForegroundHandle():X} match={GetForegroundHandle() == content.Hwnd}");
        if (TryClick(content, tabX, tabY, "日常 tab"))
        {
            _onClick?.Invoke(new DrawingPoint(tabX, tabY), content);
            RecordClick(content);
            Debug.WriteLine(
                $"[ZZZ-Daily] CLICKED tab at ({tabX},{tabY}) selectedScore={selScore:F3} notSelectedScore={noSelScore:F3}");
        }

        _sequence = 1;
        Debug.WriteLine("[ZZZ-Daily] seq=0 → seq=1 (clicked tab)");
    }

    private void ExecuteQianWangOcr(ZzzCaptureContent content)
    {
        var safeRoi = ClampRoi(QianWangRoi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] seq=1 skip frame={content.FrameIndex}: navigate ROI empty after clamp safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height})");
            return;
        }

        Debug.WriteLine(
            $"[ZZZ-Daily] seq=1 OCR begin frame={content.FrameIndex} safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height})");

        using var roi = new Mat(content.Image, safeRoi);
        OcrResult ocrResult;
        try
        {
            ocrResult = Ocr.OcrResult(roi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Daily] seq=1 OCR error: {ex.Message}");
            return;
        }

        Debug.WriteLine($"[ZZZ-Daily] seq=1 OCR done regions={ocrResult.Regions.Length}");
        foreach (var region in ocrResult.Regions)
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] seq=1 OCR region text=\"{region.Text}\" score={region.Score:F3} rect=({region.Rect.Center.X:F1},{region.Rect.Center.Y:F1})");
            if (!region.Text.Contains("前往", StringComparison.Ordinal))
            {
                continue;
            }

            var matchRect = region.Rect.BoundingRect();
            var drawRect = new DrawingRectangle(
                safeRoi.X + matchRect.X,
                safeRoi.Y + matchRect.Y,
                matchRect.Width,
                matchRect.Height);
            _onMatch?.Invoke(drawRect, content, "前往");
            Debug.WriteLine(
                $"[ZZZ-Daily] seq=1 match 前往 at rect=({drawRect.X},{drawRect.Y},{drawRect.Width},{drawRect.Height}) text=\"{region.Text}\"");

            var center = region.Rect.Center;
            var x = Math.Clamp(safeRoi.X + (int)Math.Round(center.X), safeRoi.X,
                safeRoi.X + safeRoi.Width - 1);
            var y = Math.Clamp(safeRoi.Y + (int)Math.Round(center.Y), safeRoi.Y,
                safeRoi.Y + safeRoi.Height - 1);
            Debug.WriteLine(
                $"[ZZZ-Daily] seq=1 try click 前往 at ({x},{y}) text=\"{region.Text}\" foreground=0x{GetForegroundHandle():X} match={GetForegroundHandle() == content.Hwnd}");
            var intervalMs = _lastClickTickMs == 0 ? -1 : GetCurrentTickMs() - _lastClickTickMs;
            if (TryClick(content, x, y, "前往"))
            {
                _onClick?.Invoke(new DrawingPoint(x, y), content);
                RecordClick(content);
                Debug.WriteLine(
                    $"[ZZZ-Daily] CLICKED 前往 at ({x},{y}) text=\"{region.Text}\" interval={intervalMs}ms");
            }

            _sequence = 2;
            Debug.WriteLine("[ZZZ-Daily] seq=1 → seq=2");
            return;
        }

        _sequence = 0;
        Debug.WriteLine("[ZZZ-Daily] seq=1 no 前往 found → finish");
        Finish("seq1 no 前往");
    }

    private void ExecuteF2Match(ZzzCaptureContent content)
    {
        _f2Template ??= LoadTemplate(F2TemplateName);
        var f2Tpl = _f2Template;
        if (f2Tpl == null)
        {
            Debug.WriteLine($"[ZZZ-Daily] seq=2 F2 template missing → finish (expect file: {F2TemplateName})");
            Finish("seq2 F2 template missing");
            return;
        }

        var safeRoi = ClampRoi(F2Roi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0
            || safeRoi.Width < f2Tpl.Width || safeRoi.Height < f2Tpl.Height)
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] seq=2 skip: ROI invalid safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height}) tpl={f2Tpl.Width}x{f2Tpl.Height} → finish");
            Finish("seq2 ROI invalid");
            return;
        }

        using var imageRoi = new Mat(content.Image, safeRoi);
        var (loc, score) = TemplateMatchHelper.MatchTemplate(imageRoi, f2Tpl, TemplateMatchModes.CCoeffNormed);
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=2 F2 score={score:F3} loc=({loc.X},{loc.Y}) safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height})");

        if (score < MatchSuccessThreshold)
        {
            Debug.WriteLine("[ZZZ-Daily] seq=2 F2 not matched → finish");
            Finish("seq2 no F2");
            return;
        }

        var absX = safeRoi.X + loc.X;
        var absY = safeRoi.Y + loc.Y;
        var matchRect = new CvRect(absX, absY, f2Tpl.Width, f2Tpl.Height);
        _onMatch?.Invoke(
            new DrawingRectangle(matchRect.X, matchRect.Y, matchRect.Width, matchRect.Height),
            content, "F2");

        TryPressFKey();
        RecordClick(content);
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=2 PRESSED F at center=({absX + f2Tpl.Width / 2},{absY + f2Tpl.Height / 2}) score={score:F3}");
        _sequence = 3;
        Debug.WriteLine("[ZZZ-Daily] seq=2 → seq=3 (F pressed)");
    }

    private void ExecuteContinueArrow(ZzzCaptureContent content)
    {
        if (!DetectContinueArrow(content, out var center))
        {
            Debug.WriteLine("[ZZZ-Daily] seq=3 continue-arrow not found → finish");
            Finish("seq3 no arrow");
            return;
        }

        var safeRoi = ClampRoi(ContinueArrowRoi, content.Image.Width, content.Image.Height);
        _onMatch?.Invoke(
            new DrawingRectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
            content, "继续对话");
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=3 continue-arrow hit at center=({center.X},{center.Y})");

        TryPressSpaceKey();
        RecordClick(content);
        Debug.WriteLine("[ZZZ-Daily] seq=3 PRESSED SPACE → seq=4");
        _sequence = 4;
    }

    /// <summary>
    /// seq 4:检测对话点单第一个选项是否存在,命中则按数字键 1 选择。
    /// 文字每次不同,靠左侧「① + ▶」图标块模板匹配定位第一个选项。
    /// </summary>
    private void ExecuteDialogOption1(ZzzCaptureContent content)
    {
        if (!DetectFirstDialogOption(content, out var center, out var score))
        {
            Debug.WriteLine($"[ZZZ-Daily] seq=4 dialog-option1 not found score={score:F3} → finish");
            Finish("seq4 no option1");
            return;
        }

        _onMatch?.Invoke(
            new DrawingRectangle(DialogOption1Roi.X, DialogOption1Roi.Y, DialogOption1Roi.Width, DialogOption1Roi.Height),
            content, "选项1");
        Debug.WriteLine(
            $"[ZZZ-Daily] seq=4 dialog-option1 hit score={score:F3} center=({center.X},{center.Y})");

        TryPressKey1();
        RecordClick(content);
        Debug.WriteLine("[ZZZ-Daily] seq=4 PRESSED 1 → seq=5");
        _sequence = 5;
    }

    /// <summary>
    /// seq 5:匹配右上角「跳过」按钮,命中则点击跳过。
    /// </summary>
    private void ExecuteSkip(ZzzCaptureContent content)
    {
        _skipTemplate ??= LoadTemplate(SkipTemplateName);
        var tpl = _skipTemplate;
        if (tpl == null)
        {
            Debug.WriteLine($"[ZZZ-Daily] seq=5 skip template missing → finish (expect: {SkipTemplateName})");
            Finish("seq5 skip template missing");
            return;
        }

        var safeRoi = ClampRoi(SkipRoi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width < tpl.Width || safeRoi.Height < tpl.Height)
        {
            Debug.WriteLine("[ZZZ-Daily] seq=5 skip ROI invalid → finish");
            Finish("seq5 ROI invalid");
            return;
        }

        using var imageRoi = new Mat(content.Image, safeRoi);
        var (loc, score) = TemplateMatchHelper.MatchTemplate(imageRoi, tpl, TemplateMatchModes.CCoeffNormed);
        Debug.WriteLine($"[ZZZ-Daily] seq=5 skip score={score:F3} loc=({loc.X},{loc.Y})");
        if (score < SkipThreshold)
        {
            Debug.WriteLine("[ZZZ-Daily] seq=5 skip not matched → finish");
            Finish("seq5 no skip");
            return;
        }

        var clickX = safeRoi.X + loc.X + tpl.Width / 2;
        var clickY = safeRoi.Y + loc.Y + tpl.Height / 2;
        _onMatch?.Invoke(
            new DrawingRectangle(safeRoi.X + loc.X, safeRoi.Y + loc.Y, tpl.Width, tpl.Height),
            content, "跳过");
        if (TryClick(content, clickX, clickY, "跳过"))
        {
            _onClick?.Invoke(new DrawingPoint(clickX, clickY), content);
            RecordClick(content);
            Debug.WriteLine($"[ZZZ-Daily] seq=5 CLICKED 跳过 at ({clickX},{clickY}) score={score:F3}");
        }

        _sequence = 6;
        Debug.WriteLine("[ZZZ-Daily] seq=5 → seq=6 (clicked skip)");
    }

    /// <summary>
    /// seq 6:匹配屏幕中央电池奖励弹窗,命中则按 ESC 关闭。
    /// </summary>
    private void ExecuteBattery(ZzzCaptureContent content)
    {
        _batteryTemplate ??= LoadTemplate(BatteryTemplateName);
        var tpl = _batteryTemplate;
        if (tpl == null)
        {
            Debug.WriteLine($"[ZZZ-Daily] seq=6 battery template missing → finish (expect: {BatteryTemplateName})");
            Finish("seq6 battery template missing");
            return;
        }

        var safeRoi = ClampRoi(BatteryRoi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width < tpl.Width || safeRoi.Height < tpl.Height)
        {
            Debug.WriteLine("[ZZZ-Daily] seq=6 battery ROI invalid → finish");
            Finish("seq6 ROI invalid");
            return;
        }

        using var imageRoi = new Mat(content.Image, safeRoi);
        var (loc, score) = TemplateMatchHelper.MatchTemplate(imageRoi, tpl, TemplateMatchModes.CCoeffNormed);
        Debug.WriteLine($"[ZZZ-Daily] seq=6 battery score={score:F3} loc=({loc.X},{loc.Y})");
        if (score < BatteryThreshold)
        {
            Debug.WriteLine("[ZZZ-Daily] seq=6 battery not matched → finish");
            Finish("seq6 no battery");
            return;
        }

        _onMatch?.Invoke(
            new DrawingRectangle(safeRoi.X + loc.X, safeRoi.Y + loc.Y, tpl.Width, tpl.Height),
            content, "电池奖励");
        Debug.WriteLine($"[ZZZ-Daily] seq=6 battery hit score={score:F3}");

        TryPressEscKey();
        RecordClick(content);
        Debug.WriteLine("[ZZZ-Daily] seq=6 PRESSED ESC → 序列完成");
        Finish("完成 (seq6 esc pressed)");
    }

    private static void TryPressKey1()
    {
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_1);
        Thread.Sleep(KeyPressHoldMs);
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_1);
        Debug.WriteLine($"[ZZZ-Daily] pressed VK_1 hold={KeyPressHoldMs}ms");
    }

    private static void TryPressEscKey()
    {
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_ESCAPE);
        Thread.Sleep(KeyPressHoldMs);
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_ESCAPE);
        Debug.WriteLine($"[ZZZ-Daily] pressed VK_ESCAPE hold={KeyPressHoldMs}ms");
    }

    private static void TryPressFKey()
    {
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_F);
        Thread.Sleep(KeyPressHoldMs);
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_F);
        Debug.WriteLine($"[ZZZ-Daily] pressed VK_F hold={KeyPressHoldMs}ms");
    }

    private static void TryPressSpaceKey()
    {
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_SPACE);
        Thread.Sleep(KeyPressHoldMs);
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_SPACE);
        Debug.WriteLine($"[ZZZ-Daily] pressed VK_SPACE hold={KeyPressHoldMs}ms");
    }

    /// <summary>
    /// 检测右下角"继续对话"箭头 »»。箭头为半透明白灰,用无彩色(低饱和)+高亮度掩膜识别。
    /// 命中返回 true,center 为点击点(ROI 中心)。未接入 OnCapture 流程,供后续调用。
    /// </summary>
    private static bool DetectContinueArrow(ZzzCaptureContent content, out DrawingPoint center)
    {
        center = default;
        var safeRoi = ClampRoi(ContinueArrowRoi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
        {
            return false;
        }

        using var sub = new Mat(content.Image, safeRoi);
        using var hsv = new Mat();
        Cv2.CvtColor(sub, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(0, 0, ContinueArrowVMin),
            new Scalar(179, ContinueArrowSMax, 255),
            mask);

        var count = Cv2.CountNonZero(mask);
        Debug.WriteLine(
            $"[ZZZ-Daily] continue-arrow pixels={count} roi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height})");
        if (count < ContinueArrowMinPixels || count > ContinueArrowMaxPixels)
        {
            return false;
        }

        center = new DrawingPoint(
            safeRoi.X + safeRoi.Width / 2,
            safeRoi.Y + safeRoi.Height / 2);
        return true;
    }

    /// <summary>
    /// 检测右侧对话点单的第一个选项框。文字每次不同,只对左侧固定的「① + ▶」图标块做模板匹配。
    /// 命中返回 true,center 为要点击的第一个选项框中心。模板缺失/未命中返回 false(不抛)。
    /// </summary>
    private bool DetectFirstDialogOption(ZzzCaptureContent content, out DrawingPoint center, out double score)
    {
        center = DialogOption1ClickPoint;
        score = 0;

        _dialogOption1Template ??= LoadTemplate(DialogOption1TemplateName);
        var tpl = _dialogOption1Template;
        if (tpl == null)
        {
            Debug.WriteLine($"[ZZZ-Daily] dialog-option1 template missing (expect: {DialogOption1TemplateName})");
            return false;
        }

        var safeRoi = ClampRoi(DialogOption1Roi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width < tpl.Width || safeRoi.Height < tpl.Height)
        {
            Debug.WriteLine(
                $"[ZZZ-Daily] dialog-option1 ROI too small safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height}) tpl={tpl.Width}x{tpl.Height}");
            return false;
        }

        using var imageRoi = new Mat(content.Image, safeRoi);
        var (loc, matchScore) = TemplateMatchHelper.MatchTemplate(imageRoi, tpl, TemplateMatchModes.CCoeffNormed);
        score = matchScore;
        Debug.WriteLine(
            $"[ZZZ-Daily] dialog-option1 score={matchScore:F3} loc=({loc.X},{loc.Y}) safeRoi=({safeRoi.X},{safeRoi.Y},{safeRoi.Width},{safeRoi.Height})");
        return matchScore >= DialogOption1Threshold;
    }

    private static Mat? LoadTemplate(string fileName)
    {
        var path = Global.Absolute($"{TemplateFolder}\\{fileName}");
        try
        {
            var template = new Mat(path, ImreadModes.Color);
            if (template.Empty())
            {
                template.Dispose();
                Debug.WriteLine($"[ZZZ-Daily] template is empty: {path}");
                return null;
            }

            Debug.WriteLine($"[ZZZ-Daily] loaded template: {path} size={template.Width}x{template.Height}");
            return template;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Daily] template load error: {path}: {ex.Message}");
            return null;
        }
    }

    private static bool TryMatch(ZzzCaptureContent content, Mat template, CvRect roi, out double score)
    {
        score = 0;
        var safeRoi = ClampRoi(roi, content.Image.Width, content.Image.Height);
        if (safeRoi.Width < template.Width || safeRoi.Height < template.Height)
        {
            return false;
        }

        using var imageRoi = new Mat(content.Image, safeRoi);
        var (loc, matchScore) = TemplateMatchHelper.MatchTemplate(
            imageRoi, template, TemplateMatchModes.CCoeffNormed);
        score = matchScore;
        return score >= MatchSuccessThreshold;
    }

    private static CvRect ClampRoi(CvRect roi, int width, int height)
    {
        var x1 = Math.Max(0, roi.X);
        var y1 = Math.Max(0, roi.Y);
        var x2 = Math.Min(width, roi.X + roi.Width);
        var y2 = Math.Min(height, roi.Y + roi.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return new CvRect(0, 0, 0, 0);
        }

        return new CvRect(x1, y1, x2 - x1, y2 - y1);
    }

    private static nint GetForegroundHandle()
    {
        return (nint)User32.GetForegroundWindow();
    }

    private static bool IsForeground(ZzzCaptureContent content)
    {
        return content.Hwnd != 0 && GetForegroundHandle() == content.Hwnd;
    }

    private static bool TryClick(ZzzCaptureContent content, int x, int y, string label)
    {
        var preDelayMs = Random.Shared.Next(ClickPreDelayMinMs, ClickPreDelayMaxMs + 1);
        Thread.Sleep(preDelayMs);

        var captureRect = content.CaptureRect;
        var screenX = x + (captureRect?.Left ?? 0);
        var screenY = y + (captureRect?.Top ?? 0);
        var screenWidth = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var screenHeight = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        if (screenWidth <= 1 || screenHeight <= 1)
        {
            return false;
        }

        var absX = screenX * 65535.0 / (screenWidth - 1);
        var absY = screenY * 65535.0 / (screenHeight - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
        Debug.WriteLine(
            $"[ZZZ-Daily] click {label} at screen=({screenX},{screenY}) preDelay={preDelayMs}ms foreground=0x{GetForegroundHandle():X} match={GetForegroundHandle() == content.Hwnd}");
        return true;
    }

    private bool IsInCooldown()
    {
        return GetCurrentTickMs() < _nextClickTickMs;
    }

    private void RecordClick(ZzzCaptureContent content)
    {
        _nextClickTickMs = GetCurrentTickMs() + ClickCooldownMs;
        _lastClickTickMs = GetCurrentTickMs();
        _lastClickFrame = content.FrameIndex;
    }

    private static long GetCurrentTickMs()
    {
        return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
    }

    public void Dispose()
    {
        _selectedTemplate?.Dispose();
        _selectedTemplate = null;
        _notSelectedTemplate?.Dispose();
        _notSelectedTemplate = null;
        _f2Template?.Dispose();
        _f2Template = null;
        _dialogOption1Template?.Dispose();
        _dialogOption1Template = null;
        _skipTemplate?.Dispose();
        _skipTemplate = null;
        _batteryTemplate?.Dispose();
        _batteryTemplate = null;
        GC.SuppressFinalize(this);
    }
}