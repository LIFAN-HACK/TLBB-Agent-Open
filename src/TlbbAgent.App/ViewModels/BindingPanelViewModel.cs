using System.Windows.Media;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;
public partial class BindingPanelViewModel : ObservableObject
{
    private readonly IWindowBindingService _binding;
    private readonly IWindowCaptureService _capture;
    private readonly ILogService _log;

    public BindingPanelViewModel(IWindowBindingService binding, IWindowCaptureService capture, ILogService log)
    {
        _binding = binding;
        _capture = capture;
        _log = log;
        _binding.CurrentChanged += (_, w) => UiThread.Dispatch(() =>
        {
            Current = w;
            OnPropertyChanged(nameof(HasBinding));
            OnPropertyChanged(nameof(Is64Bit));
            OnPropertyChanged(nameof(IsNonStableClientArea));
            OnPropertyChanged(nameof(AutomationAllowed));
            OnPropertyChanged(nameof(HasAutomationBlock));
            OnPropertyChanged(nameof(AutomationBlockReason));
            OnPropertyChanged(nameof(HasResolutionWarning));
            OnPropertyChanged(nameof(ResolutionWarningReason));
            StatusText = w is null ? "未绑定。" : $"已绑定：{w.ClientSizeText}";
            UnbindCommand.NotifyCanExecuteChanged();
            RefreshAvatarCommand.NotifyCanExecuteChanged();
            CaptureAvatar();
        });
    }

    [ObservableProperty] private BoundGameWindow? _current;
    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private string _statusText = "未绑定。";

    public bool HasBinding => Current is not null;
    public bool HasAvatar => Avatar is not null;
    public bool Is64Bit => Current?.Bitness == ClientBitness.X64;
    public bool IsNonStableClientArea => Current is { IsStableClientArea: false };
    public bool AutomationAllowed => Current?.IsAutomationAllowed == true;
    public bool HasAutomationBlock => Current is not null && !AutomationAllowed;
    public string AutomationBlockReason => Current?.AutomationBlockReason ?? string.Empty;
    public bool HasResolutionWarning => Current?.IsResolutionCompatibilityMode == true;
    public string ResolutionWarningReason => Current?.ResolutionWarningReason ?? string.Empty;
    public void BindAtScreenPoint(double x, double y) => _binding.BindByScreenPoint(x, y);

    [RelayCommand]
    private void BindGame()
    {
        var window = _binding.BindFirstGameWindow();
        StatusText = window is null
            ? "未找到游戏窗口。"
            : $"已绑定：{window.ClientSizeText}";
    }

    private void CaptureAvatar()
    {
        if (Current is null) { Avatar = null; OnPropertyChanged(nameof(HasAvatar)); return; }
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                Avatar = null;
                _log.Trace("Capture", "跳过头像刷新。", Current.Id);
                OnPropertyChanged(nameof(HasAvatar));
                return;
            }

            Avatar = CaptureImaging.ToBitmapSource(_capture.CaptureAvatar(Current.Hwnd));
            if (Avatar is null) _log.Warn("Capture", "头像获取失败。", Current.Id);
        }
        catch
        {
            _log.Warn("Capture", "头像获取失败。", Current.Id);
            Avatar = null;
        }
        OnPropertyChanged(nameof(HasAvatar));
    }

    [RelayCommand(CanExecute = nameof(HasBinding))]
    private void RefreshAvatar() => CaptureAvatar();

    [RelayCommand(CanExecute = nameof(HasBinding))]
    private void Unbind() => _binding.Unbind();
}
