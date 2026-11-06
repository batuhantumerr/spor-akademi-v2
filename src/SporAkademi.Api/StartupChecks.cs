using System.Globalization;

namespace SporAkademi.Api;

/// <summary>
/// Fails fast when the runtime image lacks ICU or tzdata (DEC-081). Without them, date
/// rules in Europe/Istanbul and Turkish case-insensitive search silently misbehave.
/// </summary>
internal static class StartupChecks
{
    public static void EnsureRuntimeSupportsTurkishLocale()
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        if (!string.Equals("i".ToUpper(turkish), "İ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ICU data for tr-TR is missing; Turkish casing rules are unavailable.");
        }
    }
}
