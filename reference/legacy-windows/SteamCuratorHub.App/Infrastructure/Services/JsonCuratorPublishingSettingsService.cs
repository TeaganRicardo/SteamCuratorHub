using System.IO;
using System.Text.Json;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Application.Services;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Stores public Curator identifiers and group footers in a dedicated settings
/// file. Steam credentials and browser-session data are never accepted here.
/// </summary>
public sealed class JsonCuratorPublishingSettingsService : ICuratorPublishingSettingsService
{
    private readonly object _syncRoot = new();
    private readonly string _path;

    public JsonCuratorPublishingSettingsService(string? path = null)
    {
        _path = ResolvePath(path);
    }

    public string Path => _path;

    public CuratorPublishingSettings Load()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_path))
                    return CuratorPublishingSettings.Empty;
                var settings = JsonSerializer.Deserialize<CuratorPublishingSettings>(File.ReadAllText(_path));
                return settings is null ? CuratorPublishingSettings.Empty : Normalize(settings);
            }
            catch (IOException)
            {
                return CuratorPublishingSettings.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return CuratorPublishingSettings.Empty;
            }
            catch (JsonException)
            {
                return CuratorPublishingSettings.Empty;
            }
            catch (ArgumentException)
            {
                return CuratorPublishingSettings.Empty;
            }
        }
    }

    public void Save(CuratorPublishingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        lock (_syncRoot)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized));
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (IOException)
            {
                // Publishing preferences must never interrupt draft persistence.
            }
            catch (UnauthorizedAccessException)
            {
                // The in-memory selection remains usable for a read-only profile.
            }
        }
    }

    private static string ResolvePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return System.IO.Path.GetFullPath(path);
        var overridePath = Environment.GetEnvironmentVariable("STEAM_CURATOR_PUBLISHING_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && System.IO.Path.IsPathFullyQualified(overridePath))
            return System.IO.Path.GetFullPath(overridePath);
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamCuratorHub",
            "curator-publishing.json");
    }

    private static CuratorPublishingSettings Normalize(CuratorPublishingSettings settings)
    {
        var groups = (settings.Groups ?? [])
            .Where(group => group is not null)
            .Select(NormalizeGroup)
            .Where(group => group is not null)
            .Cast<CuratorGroupProfile>()
            .DistinctBy(group => group.Id, StringComparer.Ordinal)
            .Take(50)
            .ToArray();
        var selectedGroupId = groups.Any(group => string.Equals(group.Id, settings.SelectedGroupId, StringComparison.Ordinal))
            ? settings.SelectedGroupId
            : null;
        return new CuratorPublishingSettings
        {
            CuratorIdentifier = NormalizeCuratorIdentifier(settings.CuratorIdentifier),
            SelectedGroupId = selectedGroupId,
            Groups = groups
        };
    }

    private static CuratorGroupProfile? NormalizeGroup(CuratorGroupProfile group)
    {
        var id = group.Id?.Trim() ?? string.Empty;
        var name = group.Name?.Trim() ?? string.Empty;
        if (id.Length is < 1 or > 64
            || name.Length is < 1 or > 100
            || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return null;
        return group with
        {
            Id = id,
            Name = name,
            CuratorIdentifier = NormalizeCuratorIdentifier(group.CuratorIdentifier),
            FooterEnglish = NormalizeFooter(group.FooterEnglish),
            FooterChinese = NormalizeFooter(group.FooterChinese)
        };
    }

    private static string NormalizeCuratorIdentifier(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length is >= 1 and <= 100 && trimmed.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? trimmed
            : string.Empty;
    }

    private static string NormalizeFooter(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (ReviewContentComposer.CountTextElements(normalized) > ReviewContentComposer.LongReviewCharacterLimit)
            throw new ArgumentOutOfRangeException(nameof(value), "The Curator footer is too long.");
        return normalized;
    }
}
