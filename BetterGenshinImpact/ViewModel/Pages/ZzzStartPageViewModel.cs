using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Zzz;
using BetterGenshinImpact.Genshin.Paths;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Extensions;
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fischless.GameCapture;
using Vanara.PInvoke;
using Wpf.Ui.Violeta.Controls;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class ZzzStartPageViewModel : ViewModel
{
    [ObservableProperty] private IEnumerable<EnumItem<CaptureModes>> _modeNames = EnumExtensions.ToEnumItems<CaptureModes>();

    [ObservableProperty] private string? _selectedMode = CaptureModes.BitBlt.ToString();

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private string _statusText = "已停止";

    public AllConfig Config { get; set; }

    private readonly ZzzTaskTriggerDispatcher _dispatcher = new();

    private readonly ZzzDailyTaskRunner _dailyTaskRunner;

    public ZzzStartPageViewModel(IConfigService configService)
    {
        Config = configService.Get();
        _dispatcher.UiTaskStopTickEvent += OnUiTaskStopTick;
        Config.PropertyChanged += OnConfigPropertyChanged;
        // 启动时从 Config.ZzzCaptureMode 回填。
        if (!string.IsNullOrEmpty(Config.ZzzCaptureMode))
        {
            _selectedMode = Config.ZzzCaptureMode;
        }
        ReadZzzInstallPath();
        _dailyTaskRunner = new ZzzDailyTaskRunner(Config);
    }

    private void OnUiTaskStopTick(object? sender, EventArgs e)
    {
        IsRunning = false;
        StatusText = "已停止";
    }

    private void OnConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AllConfig.ZzzTestTriggerEnabled))
        {
            _dispatcher.SetTestTriggerEnabled(Config.ZzzTestTriggerEnabled);
        }
        else if (e.PropertyName == nameof(AllConfig.ZzzCommonTriggerEnabled))
        {
            _dispatcher.SetCommonTriggerEnabled(Config.ZzzCommonTriggerEnabled);
        }
        else if (e.PropertyName == nameof(AllConfig.ZzzNewTriggerEnabled))
        {
            _dispatcher.SetNewTriggerEnabled(Config.ZzzNewTriggerEnabled);
        }
    }

    partial void OnSelectedModeChanged(string? value)
    {
        // 用户变更时写回 config(AllConfig.OnAnyChangedAction 自动 Save)。
        if (!string.IsNullOrEmpty(value))
        {
            Config.ZzzCaptureMode = value;
        }
    }

    private CaptureModes GetCaptureMode()
    {
        try
        {
            return string.IsNullOrEmpty(Config.ZzzCaptureMode)
                ? (SelectedMode ?? CaptureModes.BitBlt.ToString()).ToCaptureMode()
                : Config.ZzzCaptureMode.ToCaptureMode();
        }
        catch
        {
            return CaptureModes.BitBlt;
        }
    }

    [RelayCommand]
    private void OnStart()
    {
        var hWnd = SystemControl.FindZzzHandle();
        if (hWnd == IntPtr.Zero)
        {
            Toast.Warning("未找到绝区零窗口");
            return;
        }

        Start(hWnd);
    }

    [RelayCommand]
    private void OnStop()
    {
        _dispatcher.Stop();
        IsRunning = false;
        StatusText = "已停止";
    }

    [RelayCommand]
    private void OnManualPickWindow()
    {
        var picker = new PickerWindow();
        if (picker.PickCaptureTarget(new WindowInteropHelper(UIDispatcherHelper.MainWindow).Handle, out var hWnd))
        {
            if (hWnd != IntPtr.Zero)
            {
                Start(hWnd);
            }
            else
            {
                Toast.Error("选择的窗体句柄为空！");
            }
        }
    }

    [RelayCommand]
    private Task OnExecuteDailyTaskAsync()
    {
        // 主流程(检查进程 → 找路径 → 启动 → 等「点击进入游戏」OCR + 点击)抽到 ZzzDailyTaskRunner。
        // 这里只透传当前 dropdown 值,dropdown 改变时已通过 OnSelectedModeChanged 写回 Config.ZzzCaptureMode。
        return _dailyTaskRunner.RunAsync(SelectedMode);
    }

    private void ReadZzzInstallPath()
    {
        if (string.IsNullOrEmpty(Config.ZzzInstallPath))
        {
            Task.Run(() =>
            {
                var p = RegistryGameLocator.GetDefaultZzzInstallPath();
                if (!string.IsNullOrEmpty(p))
                {
                    Config.ZzzInstallPath = p;
                }
            });
        }
    }

    [RelayCommand]
    private void OnCaptureCurrentImage()
    {
        var sw = Stopwatch.StartNew();
        var picker = new PickerWindow(true);

        if (!picker.PickCaptureTarget(new WindowInteropHelper(UIDispatcherHelper.MainWindow).Handle, out var hWnd))
        {
            Debug.WriteLine($"[ZZZ-GetImage] 用户取消选择窗口 elapsed={sw.ElapsedMilliseconds}ms");
            return;
        }

        if (hWnd == IntPtr.Zero)
        {
            Debug.WriteLine($"[ZZZ-GetImage] 选择的窗体句柄为空 elapsed={sw.ElapsedMilliseconds}ms");
            ThemedMessageBox.Error("选择的窗体句柄为空");
            return;
        }

        var captureMode = GetCaptureMode();
        var (title, processName, pid) = DescribeWindow(hWnd);
        Debug.WriteLine($"[ZZZ-GetImage] mode={captureMode} hWnd=0x{hWnd:X} title=\"{title}\" process={processName}({pid})");

        // 用 ZZZ 启动页的截图模式,而不是原神的 Config.CaptureMode
        // capture 必须由 Loop 回调自己 Dispose,因为 CompositionTarget.Rendering 是异步延迟触发的,
        // finally 会让 capture 在 Loop 跑到第 3 帧前就被释放。
        var capture = GameCaptureFactory.Create(captureMode);
        try
        {
            capture.Start(hWnd, new Dictionary<string, object>()
            {
                { "autoFixWin11BitBlt", false },
            });
            Debug.WriteLine($"[ZZZ-GetImage] capture.Start OK IsCapturing={capture.IsCapturing} elapsed={sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-GetImage] capture.Start FAILED: {ex.GetType().Name}: {ex.Message} elapsed={sw.ElapsedMilliseconds}ms");
            Debug.WriteLine(ex.StackTrace);
            ThemedMessageBox.Error($"捕获启动失败: {ex.Message}");
            capture.Dispose();
            return;
        }

        // 使用 CompositionTarget.Rendering 等待几帧后再获取图像
        int frameCount = 0;
        var window = new CaptureCurrentImageWindow();
        window.Show(); // 先显示窗口

        void Loop(object? sender, EventArgs e)
        {
            frameCount++;
            if (frameCount == 3)
            {
                Debug.WriteLine($"[ZZZ-GetImage] 到达第 {frameCount} 帧 elapsed={sw.ElapsedMilliseconds}ms IsCapturing={capture.IsCapturing}");
            }
            // 等待 3 帧后再尝试捕获,确保 capture 初始化完成
            if (frameCount >= 3)
            {
                CompositionTarget.Rendering -= Loop; // 先取消订阅

                GameCaptureFrame? frame = null;
                try
                {
                    frame = capture.Capture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-GetImage] capture.Capture THREW: {ex.GetType().Name}: {ex.Message} frameCount={frameCount} IsCapturing={capture.IsCapturing} elapsed={sw.ElapsedMilliseconds}ms");
                    Debug.WriteLine(ex.StackTrace);
                }

                if (frame != null)
                {
                    using (frame)
                    {
                        var mat = frame.Frame;
                        Debug.WriteLine(
                            $"[ZZZ-GetImage] Capture OK mat={mat.Width}x{mat.Height} channels={mat.Channels} frameCount={frameCount} IsCapturing={capture.IsCapturing} elapsed={sw.ElapsedMilliseconds}ms");
                        window.SetImage(mat.ToWriteableBitmap());
                    }
                }
                else
                {
                    Debug.WriteLine(
                        $"[ZZZ-GetImage] Capture 返回 null frameCount={frameCount} IsCapturing={capture.IsCapturing} elapsed={sw.ElapsedMilliseconds}ms");
                    window.Close();
                    ThemedMessageBox.Error("捕获图像失败");
                }

                try
                {
                    capture.Stop();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-GetImage] capture.Stop 异常: {ex.Message}");
                }

                capture.Dispose();
            }
        }

        CompositionTarget.Rendering += Loop;
    }

    /// <summary>
    /// 读取窗口标题 + 进程名/PID,用于诊断"获取当前图像"选错窗口的情况。
    /// </summary>
    private static (string Title, string ProcessName, int Pid) DescribeWindow(nint hWnd)
    {
        string title = "";
        string processName = "?";
        int pid = 0;
        try
        {
            var sb = new StringBuilder(256);
            if (User32.GetWindowText(hWnd, sb, sb.Capacity) > 0)
            {
                title = sb.ToString();
            }

            if (User32.GetWindowThreadProcessId(hWnd, out var procId) != 0)
            {
                pid = (int)procId;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    processName = p.ProcessName;
                }
                catch
                {
                    // 进程可能已退出
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-GetImage] DescribeWindow 异常: {ex.Message}");
        }
        return (title, processName, pid);
    }

    private void Start(IntPtr hWnd)
    {
        _dispatcher.Start(hWnd, GetCaptureMode(), Config.ZzzTestTriggerEnabled, Config.ZzzCommonTriggerEnabled, Config.ZzzNewTriggerEnabled);
        IsRunning = true;
        StatusText = "运行中111";
    }
}
