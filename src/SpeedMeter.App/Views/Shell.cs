using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpeedMeter.App.Theme;

namespace SpeedMeter.App.Views;

/// <summary>Small bridges to Windows: Explorer, a message box.</summary>
public static class Shell
{
    public static void OpenFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Explorer not starting is not worth a crash.
        }
    }

    public static async Task ShowMessage(Window window, string title, string message)
    {
        if (window.Content?.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Ui.Paragraph(message, 14.5, Palette.TextMutedBrush),
            CloseButtonText = "OK",
            XamlRoot = window.Content.XamlRoot,
            RequestedTheme = Palette.IsLight ? ElementTheme.Light : ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }
}
