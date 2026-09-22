using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Application.Abstractions;

/// <summary>Persistence boundary for review tasks and their child records.</summary>
public interface IReviewTaskRepository
{
    IReadOnlyList<ReviewTask> GetAll();
    ReviewTask? GetById(Guid id);
    void Add(ReviewTask task);
    void Update(ReviewTask task);
    /// <summary>Atomically makes one task current, or clears the current task when the id is null.</summary>
    void SetCurrent(Guid? id);
    void Delete(Guid id);
}
