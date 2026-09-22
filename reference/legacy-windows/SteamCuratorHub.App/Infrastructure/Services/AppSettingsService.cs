using System.IO;
using System.Text.Json;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Stores non-sensitive desktop preferences. Steam credentials and browser
/// session data are intentionally outside this file.
/// </summary>
public static class AppSettingsService
{
    private static readonly object SyncRoot = new();
    private static string SettingsPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("STEAM_CURATOR_SETTINGS_PATH");
            return !string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath)
                ? Path.GetFullPath(overridePath)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamCuratorHub",
                    "settings.json");
        }
    }

    public static string LoadLanguage()
    {
        lock (SyncRoot)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return "zh-CN";

                var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath));
                return NormalizeLanguage(settings?.Language);
            }
            catch (IOException)
            {
                return "zh-CN";
            }
            catch (UnauthorizedAccessException)
            {
                return "zh-CN";
            }
            catch (JsonException)
            {
                return "zh-CN";
            }
        }
    }

    public static void SaveLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        lock (SyncRoot)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = SettingsPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new DesktopSettings(normalized)));
                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            catch (IOException)
            {
                // A preference write should never interrupt the review workflow.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the in-memory language when the profile is read-only.
            }
        }
    }

    private static string NormalizeLanguage(string? language)
        => string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";

    private sealed record DesktopSettings(string Language);
}
