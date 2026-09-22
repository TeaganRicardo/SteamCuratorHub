using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.Mvvm;

namespace SteamCuratorHub.Presentation.ViewModels;

/// <summary>An editable presentation model for one persisted Curator profile.</summary>
public sealed class CuratorGroupViewModel : ObservableObject
{
    private string _name;
    private string _curatorIdentifier;
    private string _footerEnglish;
    private string _footerChinese;

    public CuratorGroupViewModel(CuratorGroupProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Id = profile.Id;
        _name = profile.Name;
        _curatorIdentifier = profile.CuratorIdentifier;
        _footerEnglish = profile.FooterEnglish;
        _footerChinese = profile.FooterChinese;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string CuratorIdentifier
    {
        get => _curatorIdentifier;
        set => SetProperty(ref _curatorIdentifier, value ?? string.Empty);
    }

    public string FooterEnglish
    {
        get => _footerEnglish;
        set => SetProperty(ref _footerEnglish, value ?? string.Empty);
    }

    public string FooterChinese
    {
        get => _footerChinese;
        set => SetProperty(ref _footerChinese, value ?? string.Empty);
    }

    public CuratorGroupProfile ToModel() => new()
    {
        Id = Id,
        Name = Name,
        CuratorIdentifier = CuratorIdentifier,
        FooterEnglish = FooterEnglish,
        FooterChinese = FooterChinese
    };

    public string GetFooter(string language) => ToModel().GetFooter(language);
}
