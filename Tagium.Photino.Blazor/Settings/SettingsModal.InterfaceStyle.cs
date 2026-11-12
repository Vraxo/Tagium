using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.AppShell;
using Tagium.Photino.Blazor.Notifications;

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

        bool requiresRestart = Program.SupportsNativeMica
            && style != Program.StartupInterfaceStyle
            && (Program.UsesMicaChrome || style == InterfaceStyle.Windows11);

        if (requiresRestart)
        {
            Toast.Show(
                "Interface style saved. Restart Tagium to apply the Windows 11 window composition changes.",
                ToastType.Info);
            return;
        }

        await JS.InvokeVoidAsync("themeManager.applyInterfaceStyle", style.ToString());
    }
}
