namespace Argus.App.Services;

/// <summary>
/// Defines the system-tray contract.
/// A full WPF-native implementation can use the Hardcodet.NotifyIcon.Wpf
/// NuGet package or a direct Win32 Shell_NotifyIcon P/Invoke wrapper.
/// No WinForms (System.Windows.Forms) required.
/// </summary>
public interface ITrayService
{
    void Show();
    void Hide();
    void SetTooltip(string tooltip);
    void ShowBalloonTip(string title, string message);
}
