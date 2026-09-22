using System.Diagnostics;
using SteamCuratorHub.Application.Abstractions;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// Delegates an owned-game launch to the registered Steam URI handler. No process
/// injection, achievement mutation, or credential handling occurs here.
/// </summary>
public sealed class SteamUriGameLauncher : ISteamGameLauncher
{
    public Task<SteamGameLaunchResult> LaunchAsync(uint appId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = CreateLaunchUri(appId).AbsoluteUri,
                UseShellExecute = true
            });
            return Task.FromResult(process is null
                ? SteamGameLaunchResult.Failed("steam-launch-not-started")
                : SteamGameLaunchResult.Started);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(SteamGameLaunchResult.Failed("steam-unavailable"));
        }
    }

    public static Uri CreateLaunchUri(uint appId)
    {
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));
        return new Uri($"steam://run/{appId}", UriKind.Absolute);
    }
}
