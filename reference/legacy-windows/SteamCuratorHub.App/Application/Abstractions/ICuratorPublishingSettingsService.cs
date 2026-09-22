using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Abstractions;

public interface ICuratorPublishingSettingsService
{
    CuratorPublishingSettings Load();
    void Save(CuratorPublishingSettings settings);
}
