using System;
using System.Collections.Generic;
using System.Windows.Interop;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.WuWa;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Extensions;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fischless.GameCapture;
using Wpf.Ui.Violeta.Controls;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class WuWaStartPageViewModel : ViewModel
{
    [ObservableProperty] private IEnumerable<EnumItem<CaptureModes>> _modeNames = EnumExtensions.ToEnumItems<CaptureModes>();

    [ObservableProperty] private string? _selectedMode = CaptureModes.BitBlt.ToString();

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private string _statusText = "已停止";

    public AllConfig Config { get; set; }

    private readonly WuWaTaskTriggerDispatcher _dispatcher;

    public WuWaStartPageViewModel(IConfigService configService, IMaskWindowHost maskWindowHost)
    {
        Config = configService.Get();
        _dispatcher = new WuWaTaskTriggerDispatcher(maskWindowHost);
        _dispatcher.UiTaskStopTickEvent += OnUiTaskStopTick;
        Config.PropertyChanged += OnConfigPropertyChanged;
        if (!string.IsNullOrEmpty(Config.WuWaCaptureMode))
        {
            _selectedMode = Config.WuWaCaptureMode;
        }
    }

    private void OnUiTaskStopTick(object? sender, EventArgs e)
    {
        IsRunning = false;
        StatusText = "已停止";
    }

    private void OnConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AllConfig.WuWaCommonTriggerEnabled))
        {
            _dispatcher.SetCommonTriggerEnabled(Config.WuWaCommonTriggerEnabled);
        }
    }

    partial void OnSelectedModeChanged(string? value)
    {
        // 用户变更时写回 config(AllConfig.OnAnyChangedAction 自动 Save)。
        if (!string.IsNullOrEmpty(value))
        {
            Config.WuWaCaptureMode = value;
        }
    }

    private CaptureModes GetCaptureMode()
    {
        try
        {
            return string.IsNullOrEmpty(Config.WuWaCaptureMode)
                ? (SelectedMode ?? CaptureModes.BitBlt.ToString()).ToCaptureMode()
                : Config.WuWaCaptureMode.ToCaptureMode();
        }
        catch
        {
            return CaptureModes.BitBlt;
        }
    }

    [RelayCommand]
    private void OnStart()
    {
        var hWnd = SystemControl.FindWuWaHandle();
        if (hWnd == IntPtr.Zero)
        {
            Toast.Warning("未找到鸣潮窗口,请先启动鸣潮或使用“选择捕获窗口”手动指定");
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

    private void Start(IntPtr hWnd)
    {
        _dispatcher.Start(hWnd, GetCaptureMode(), Config.WuWaCommonTriggerEnabled);
        IsRunning = true;
        StatusText = "运行中";
    }
}
