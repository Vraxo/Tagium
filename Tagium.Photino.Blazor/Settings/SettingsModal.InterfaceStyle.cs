using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.AppShell;
using Tagium.Photino.Blazor.Notifications;

namespace Tagium.Photino.Blazor.Settings;

public partial class SettingsModal
{
    private void UpdateInterfaceStyle(ChangeEventArgs args)
    {
        if (!Enum.TryParse(args.Value?.ToString(), out InterfaceStyle style)
            || !Enum.IsDefined(style))
        {
            return;
        }

        Settings.Update(settings => settings.InterfaceStyle = style);

        bool requiresRestart = Program.SupportsNativeMica
            && style != Program.ActiveInterfaceStyle
            && (Program.UsesNativeMica || style == InterfaceStyle.Windows11);

        if (requiresRestart)
        {
            Toast.Show(
                "Interface style saved. Restart Tagium to apply the native Windows 11 backdrop.",
                ToastType.Info);
            return;
        }

        Program.SetActiveInterfaceStyle(style);
        Appearance.NotifyChanged();
    }
}
