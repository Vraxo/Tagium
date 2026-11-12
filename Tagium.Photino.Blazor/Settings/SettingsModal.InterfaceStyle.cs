using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.AppShell;

namespace Tagium.Photino.Blazor.Settings;

public partial class SettingsModal
{
    private async Task UpdateInterfaceStyle(ChangeEventArgs args)
    {
        if (!Enum.TryParse(args.Value?.ToString(), out InterfaceStyle style)
            || !Enum.IsDefined(style))
        {
            return;
        }

        Settings.Update(settings => settings.InterfaceStyle = style);
        await JS.InvokeVoidAsync("themeManager.applyInterfaceStyle", style.ToString());

        if (Program.MainWindow is { } window)
        {
            WindowsMicaWindow.SetEnabled(window, style == InterfaceStyle.Windows11);
        }
    }
}
