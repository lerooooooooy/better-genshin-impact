using System;
using System.Diagnostics;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 通用 trigger:对 TestZzzTaskTrigger 引用的所有主模板逐帧扫描,任一命中即执行对应动作。
///
/// 设计要点:
/// - **无状态机 / 无序列 / 无 retreat/scan**:纯单帧扫描,首命中后 return,下一帧重新全扫描。
/// - **无 cooldown / 无 per-template 间隔**:跨帧无脑重试(由 PressKeyForeground 内部 300-400ms 随机抖动兜底)。
/// - **动作全部走 <see cref="ZzzTriggerActions"/>**:与 TestZzzTaskTrigger 共享动作原语,无重复实现。
/// - **模板常量与 Test trigger 完全一致**:ROI / click rect / 阈值 / 路径都不变,两个 trigger 行为对齐。
/// - **不覆盖 seq 1 内嵌副模板**:DailySel / DailyNoSel / DaliyFin / DaliyReach 只在 Test trigger 的 seq 1 内部用,
///   它们的动作依赖上下文(双匹配 + 完成度判断),不在通用一次性扫描里复刻。
/// </summary>
public sealed class CommonZzzTaskTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "CommonZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    // 与 TestZzzTaskTrigger 同值
    private const double MatchThreshold = 0.96;

    // 模板路径(20 个主模板,与 TestZzzTaskTrigger._scanTargets 完全一致)
    private const string F2Path = "Assets\\Template\\Daily\\daliyF2_1538_116_36x24_200.png";
    private const string DaliyUiPath = "Assets\\Template\\Daily\\daliyUi_1701_221_54x290.png";
    private const string DaliyGoPath = "Assets\\Template\\Daily\\daliyGo_871_843_181x47.png";
    private const string DialogOpt1Path = "Assets\\Template\\Daily\\dialogOpt1_1391_573_16x29_200.png";
    private const string Rwd1Path = "Assets\\Template\\Daily\\rwd1_1161_510_74x58_400.png";
    private const string GetBatteryPath = "Assets\\Template\\Daily\\getBattery_907_464_105x105.png";
    // "rin" 红色 logo 模板 (rwd2)
    private const string RinPath = "Assets\\Template\\Daily\\rwd2_1013_632_201x119.png";
    private const string Rwd3Path = "Assets\\Template\\Daily\\rwd3_1047_662_112x96.png";
    private const string Rwd4Path = "Assets\\Template\\Daily\\rwd4_927_406_63x33.png";
    private const string Rwd5Path = "Assets\\Template\\Daily\\rwd5_934_855_56x26.png";
    private const string Rwd6Path = "Assets\\Template\\Daily\\rwd6_1257_955_121x34.png";
    private const string Rwd7Path = "Assets\\Template\\Daily\\rwd7_543_233_115x29.png";
    private const string Shop2Path = "Assets\\Template\\Daily\\shop2_845_575_115x22.png";
    private const string Shop3Path = "Assets\\Template\\Daily\\shop3_68_1012_118x29.png";
    private const string Shop4Path = "Assets\\Template\\Daily\\shop4_1056_630_90x228.png";
    private const string Shop5Path = "Assets\\Template\\Daily\\shop5_1282_1010_187x34.png";
    private const string Shop6Path = "Assets\\Template\\Daily\\shop6_1546_955_244x34.png";
    // "准备营业" OCR
    private static readonly CvRect ReadyToOpenRoi = new(912, 498, 123, 52);
    private static readonly CvRect ReadyToOpenClickRect = new(1029, 606, 188, 36);
    private const string ReadyToOpenText = "准备营业";
    // "诚信经营" OCR
    private static readonly CvRect HonestBusinessRoi = new(811, 507, 124, 36);
    private static readonly CvRect HonestBusinessClickRect = new(846, 604, 218, 42);
    private const string HonestBusinessText = "诚信经营";

    // 继续对话箭头 »» 七道防线(与 TestZzzTaskTrigger 同值)
    private static readonly CvRect ContinueArrowRoi = new(1462, 966, 42, 45);
    private const int ContinueArrowSMax = 40;
    private const int ContinueArrowVMin = 130;
    private const int ContinueArrowMinArea = 80;
    private const int ContinueArrowMaxArea = 400;
    private const double ContinueArrowMinAspect = 1.3;
    private const double ContinueArrowMinExtent = 0.4;
    private const double ContinueArrowMinLargestFraction = 0.5;
    private const double ContinueArrowMaxConvexity = 0.85;
    private const int ContinueArrowMinHeight = 16;
    private const int ContinueArrowMaxLabels = 2;

    // 固定点击区(模板本身 ≠ 实际按钮位置时使用)
    private static readonly CvRect GetBatteryClickRect = new(854, 709, 203, 37);
    private static readonly CvRect Shop2ClickRect = new(829, 642, 145, 228);
    private static readonly CvRect Shop3ClickRect = new(1605, 1008, 204, 38);

    // 20 个 target(字段顺序 = entries 顺序 = 命中优先级)
    private readonly TemplateWaitTarget _f2Target = new("F2", F2Path, MatchThreshold);
    private readonly TemplateWaitTarget _daliyUiTarget = new("DaliyUi", DaliyUiPath, MatchThreshold);
    private readonly TemplateWaitTarget _daliyGoTarget = new("DaliyGo", DaliyGoPath, MatchThreshold);
    private readonly HsvWaitTarget _continueArrowTarget = new("ContinueArrow",
        c => ZzzImageUtils.DetectArrowBlob(
            c.Image, ContinueArrowRoi,
            ContinueArrowSMax, ContinueArrowVMin,
            ContinueArrowMinArea, ContinueArrowMaxArea,
            ContinueArrowMinHeight,
            ContinueArrowMinAspect, ContinueArrowMinExtent,
            ContinueArrowMinLargestFraction, ContinueArrowMaxConvexity,
            ContinueArrowMaxLabels));
    private readonly TemplateWaitTarget _dialogOpt1Target = new("DialogOpt1", DialogOpt1Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd6Target = new("Rwd6", Rwd6Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd1Target = new("Rwd1", Rwd1Path, MatchThreshold);
    private readonly TemplateWaitTarget _getBatteryTarget = new("GetBattery", GetBatteryPath, MatchThreshold);
    private readonly TemplateWaitTarget _rinTarget = new("Rin", RinPath, MatchThreshold);
    private readonly TemplateWaitTarget _rwd3Target = new("Rwd3", Rwd3Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd4Target = new("Rwd4", Rwd4Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd5Target = new("Rwd5", Rwd5Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd7Target = new("Rwd7", Rwd7Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop2Target = new("Shop2", Shop2Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop3Target = new("Shop3", Shop3Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop4Target = new("Shop4", Shop4Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop5Target = new("Shop5", Shop5Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop6Target = new("Shop6", Shop6Path, MatchThreshold);
    private readonly OcrWaitTarget _readyToOpenTarget = new("ReadyToOpen", ReadyToOpenRoi, ReadyToOpenText);
    private readonly OcrWaitTarget _honestBusinessTarget = new("HonestBusiness", HonestBusinessRoi, HonestBusinessText);

    // 20 个 entry:target + label + 命中动作(委托)
    // 顺序决定优先级 —— 与 TestZzzTaskTrigger._scanTargets 完全一致(F2 排第一)
    private readonly (IZzzWaitTarget Target, string Label, Action<TemplateMatchResult?, ZzzCaptureContent> OnHit)[] _entries;

    private readonly ZzzOverlayWindow? _overlay;

    public CommonZzzTaskTrigger(ZzzOverlayWindow? overlay = null)
    {
        _overlay = overlay;
        _entries = BuildEntries();
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        // 单帧扫描所有 entry,首命中后 return(与旧 EmptyZzzTaskTrigger 的"帧内首胜出"一致),下一帧重新全扫描。
        foreach (var (target, label, onHit) in _entries)
        {
            try
            {
                bool hit;
                TemplateMatchResult? result;
                try
                {
                    hit = target.TryMatch(content, out result);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-Common] {label} TryMatch threw: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (hit)
                {
                    Debug.WriteLine($"[ZZZ-Common] HIT label={label}{ZzzWaitForHit.FormatMatchDetail(result, target.Threshold)}");
                    try
                    {
                        onHit(result, content);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ZZZ-Common] {label} OnHit threw: {ex.GetType().Name}: {ex.Message}");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Common] {label} unexpected: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // TemplateWaitTarget 持 Mat 需 Dispose;Hsv/Ocr 不持资源,无 Dispose 动作
        _f2Target.Dispose();
        _daliyUiTarget.Dispose();
        _daliyGoTarget.Dispose();
        _dialogOpt1Target.Dispose();
        _rwd6Target.Dispose();
        _rwd1Target.Dispose();
        _getBatteryTarget.Dispose();
        _rinTarget.Dispose();
        _rwd3Target.Dispose();
        _rwd4Target.Dispose();
        _rwd5Target.Dispose();
        _rwd7Target.Dispose();
        _shop2Target.Dispose();
        _shop3Target.Dispose();
        _shop4Target.Dispose();
        _shop5Target.Dispose();
        _shop6Target.Dispose();
    }

    private (IZzzWaitTarget, string, Action<TemplateMatchResult?, ZzzCaptureContent>)[] BuildEntries()
    {
        return new (IZzzWaitTarget, string, Action<TemplateMatchResult?, ZzzCaptureContent>)[]
        {
            (_f2Target, "F2", (_, c) => ZzzTriggerActions.PressKeyForeground(User32.VK.VK_F2, c)),

            (_daliyUiTarget, "DaliyUi", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/DaliyUi");
                if (r != null) ZzzTriggerActions.ClickMatchedArea(r, c);
            }),

            (_daliyGoTarget, "DaliyGo", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/DaliyGo");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_continueArrowTarget, "ContinueArrow", (_, c) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ContinueArrowRoi, c.Image.Width, c.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    c,
                    "Common/ContinueArrow");
                try
                {
                    var snapshotPath = ZzzImageUtils.SaveArrowRoiSnapshot(c.Image, ContinueArrowRoi, "common_continue_arrow");
                    Debug.WriteLine($"[ZZZ-Common] saved snapshot: {snapshotPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-Common] snapshot save failed: {ex.GetType().Name}: {ex.Message}");
                }
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_SPACE, c);
            }),

            (_dialogOpt1Target, "DialogOpt1", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/DialogOpt1");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_1, c);
            }),

            (_rwd6Target, "Rwd6", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd6");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, c);
            }),

            (_rwd1Target, "Rwd1", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd1");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_getBatteryTarget, "GetBattery", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/GetBattery");
                ZzzTriggerActions.ClickRect(c, GetBatteryClickRect);
            }),

            (_rinTarget, "Rin", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rin");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_rwd3Target, "Rwd3", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd3");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_rwd4Target, "Rwd4", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd4");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, c);
            }),

            (_rwd5Target, "Rwd5", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd5");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, c);
            }),

            (_rwd7Target, "Rwd7", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Rwd7");
                ZzzTriggerActions.PressKeyForeground(User32.VK.VK_ESCAPE, c);
            }),

            (_shop2Target, "Shop2", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Shop2");
                ZzzTriggerActions.ClickRect(c, Shop2ClickRect);
            }),

            (_shop3Target, "Shop3", (r, c) =>
            {
                if (r != null) ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Shop3");
                ZzzTriggerActions.ClickRect(c, Shop3ClickRect);
            }),

            (_shop4Target, "Shop4", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Shop4");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_shop5Target, "Shop5", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Shop5");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_shop6Target, "Shop6", (r, c) =>
            {
                if (r != null)
                {
                    ZzzTriggerActions.DrawHitRect(_overlay, r, c, "Common/Shop6");
                    ZzzTriggerActions.ClickMatchedArea(r, c);
                }
            }),

            (_readyToOpenTarget, "ReadyToOpen", (_, c) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ReadyToOpenRoi, c.Image.Width, c.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    c,
                    "Common/ReadyToOpen");
                ZzzTriggerActions.ClickRect(c, ReadyToOpenClickRect);
            }),

            (_honestBusinessTarget, "HonestBusiness", (_, c) =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(HonestBusinessRoi, c.Image.Width, c.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    c,
                    "Common/HonestBusiness");
                ZzzTriggerActions.ClickRect(c, HonestBusinessClickRect);
            }),
        };
    }
}
