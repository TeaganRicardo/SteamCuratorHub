using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamCuratorHub.Domain.Models;
public enum ReviewTaskStatus { Pending, Running, ReadyToPublish, Published, Archived }

public enum PlaythroughStatus { Stopped, Running, Paused }

public sealed class ReviewTask : INotifyPropertyChanged
{
    private Game _game = null!;
    private double _targetHours = 4;
    private DateTime _dueDate = DateTime.Today.AddDays(7);
    private double _playedHours;
    private ReviewTaskStatus _status = ReviewTaskStatus.Pending;
    private PlaythroughStatus _playthroughStatus = PlaythroughStatus.Stopped;
    private string? _curatorGroup;
    private bool _isCurrent;
    private DateTime _updatedAt = DateTime.UtcNow;
    private string? _lastError;

    public Guid Id { get; init; } = Guid.NewGuid();
    public required Game Game { get => _game; set => SetField(ref _game, value); }
    public double TargetHours { get => _targetHours; set => SetField(ref _targetHours, value); }
    public DateTime DueDate { get => _dueDate; set => SetField(ref _dueDate, value); }
    public double PlayedHours { get => _playedHours; set => SetField(ref _playedHours, value); }
    public ReviewTaskStatus Status { get => _status; set => SetField(ref _status, value); }
    public PlaythroughStatus PlaythroughStatus { get => _playthroughStatus; set => SetField(ref _playthroughStatus, value); }
    public string? CuratorGroup { get => _curatorGroup; set => SetField(ref _curatorGroup, value); }
    public bool IsCurrent { get => _isCurrent; set => SetField(ref _isCurrent, value); }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get => _updatedAt; set => SetField(ref _updatedAt, value); }
    public string? LastError { get => _lastError; set => SetField(ref _lastError, value); }

    public ObservableCollection<Achievement> Achievements { get; init; } = [];
    public List<Draft> Drafts { get; init; } = [];
    public List<Publication> Publications { get; init; } = [];
    public List<CuratorCopy> CuratorCopies { get; init; } = [];

    public Draft? LongReviewDraft => Drafts.FirstOrDefault(x => x.Type == DraftType.LongReview);
    public Draft? CuratorShortReviewDraft => Drafts.FirstOrDefault(x => x.Type == DraftType.CuratorShortReview);

    /// <summary>
    /// Finds the draft for a content type and language. A type-only fallback keeps
    /// existing drafts visible when they were created before language metadata was added.
    /// </summary>
    public Draft? GetDraft(DraftType type, string language)
    {
        var exact = Drafts.FirstOrDefault(x => x.Type == type &&
            string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase));
        return exact ?? Drafts.FirstOrDefault(x => x.Type == type);
    }

    public Draft GetOrCreateDraft(DraftType type, string language)
    {
        var draft = Drafts.FirstOrDefault(x => x.Type == type &&
            string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase));
        if (draft is not null)
            return draft;

        draft = new Draft
        {
            ReviewTaskId = Id,
            Type = type,
            Language = language
        };
        Drafts.Add(draft);
        return draft;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
