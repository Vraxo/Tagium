namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Carries change notices between the application's windows.
///
/// Settings are written to disk immediately, but a child window can hold its own
/// <see cref="Tagium.Core.Settings.SettingsManager"/> instance, which is not the instance
/// the main window's <c>AppState</c> watches. Anything the main workspace must re-read when
/// the Settings window saves — the browsing mode and page/batch size today — is announced
/// here so the live window can refresh without a restart. It is a no-op when both windows
/// really do share one instance, because re-reading the file is harmless.
/// </summary>
public static class WindowEventHub
{
    /// <summary>Raised after the Settings window saved a change the main window renders from.</summary>
    public static event Action? SettingsSaved;

    public static void NotifySettingsSaved() => SettingsSaved?.Invoke();
}
