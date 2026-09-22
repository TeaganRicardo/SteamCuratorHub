namespace SteamCuratorHub.Application.Abstractions;

/// <summary>Read-only runtime status exposed by an AI text service.</summary>
public interface IAiServiceStatus
{
    bool IsConfigured { get; }
    string ProviderName { get; }
    string Model { get; }
}
