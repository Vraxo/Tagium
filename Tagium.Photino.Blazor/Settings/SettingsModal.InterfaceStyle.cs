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

        if (Program.SupportsNativeMica && style != Program.StartupInterfaceStyle)
        {
            Toast.Show("Interface style saved. Restart Tagium to apply it.", ToastType.Success);
            return;
        }

        await JS.InvokeVoidAsync("themeManager.applyInterfaceStyle", style.ToString());
        await JS.InvokeVoidAsync("themeManager.applyNativeMica", Program.UsesMicaChrome && WindowsMicaWindow.IsActive);
    }
}
