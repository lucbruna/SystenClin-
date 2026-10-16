using Microsoft.Win32;

namespace Systenclin.Core;

/// <summary>
/// Configurações rápidas do Windows (tweaks de UX e desempenho) aplicadas
/// via registro. Cada ajuste é independente e reversível.
/// </summary>
public static class QuickSettings
{
    public sealed record Result(bool Success, int Applied, string? Error);

    public static Result Apply(bool showExtensions, bool showHidden, bool darkMode,
        bool disableAnimations, bool disableTransparency, CancellationToken ct = default)
    {
        int applied = 0;
        try
        {
            if (showExtensions)
            {
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    "HideFileExt", 0);
                applied++;
            }
            if (showHidden)
            {
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    "Hidden", 1);
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    "ShowSuperHidden", 1);
                applied++;
            }
            if (darkMode)
            {
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 0);
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 0);
                applied++;
            }
            if (disableAnimations)
            {
                SetDword(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics",
                    "MinAnimate", 0);
                applied++;
            }
            if (disableTransparency)
            {
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "EnableTransparency", 0);
                applied++;
            }

            return new Result(true, applied, null);
        }
        catch (Exception ex)
        {
            return new Result(false, applied, ex.Message);
        }
    }

    private static void SetDword(RegistryKey root, string subKey, string name, int value)
    {
        using var key = root.CreateSubKey(subKey, RegistryKeyPermissionCheck.ReadWriteSubTree);
        key?.SetValue(name, value, RegistryValueKind.DWord);
    }
}
