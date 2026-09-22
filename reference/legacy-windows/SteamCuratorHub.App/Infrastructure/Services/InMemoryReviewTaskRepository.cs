using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
namespace SteamCuratorHub.Infrastructure.Services;
public sealed class InMemoryReviewTaskRepository : IReviewTaskRepository
{
    private readonly List<ReviewTask> _tasks = [];
    private readonly object _syncRoot = new();
    public IReadOnlyList<ReviewTask> GetAll()
    {
        lock (_syncRoot)
            return _tasks.ToArray();
    }
    public ReviewTask? GetById(Guid id)
    {
        lock (_syncRoot)
            return _tasks.FirstOrDefault(x => x.Id == id);
    }
    public void Add(ReviewTask task)
    {
        lock (_syncRoot)
        {
            if (_tasks.All(x => x.Id != task.Id))
                _tasks.Add(task);
        }
    }

    public void Update(ReviewTask task)
    {
        lock (_syncRoot)
        {
            var index = _tasks.FindIndex(x => x.Id == task.Id);
            if (index >= 0)
                _tasks[index] = task;
            else
                _tasks.Add(task);
        }
    }

    public void SetCurrent(Guid? id)
    {
        lock (_syncRoot)
        {
            if (id.HasValue && _tasks.All(task => task.Id != id.Value))
                throw new KeyNotFoundException($"Task '{id.Value:D}' does not exist.");

            foreach (var task in _tasks)
                task.IsCurrent = id.HasValue && task.Id == id.Value;
        }
    }

    public void Delete(Guid id)
    {
        lock (_syncRoot)
        {
            var task = _tasks.FirstOrDefault(item => item.Id == id);
            if (task is not null)
                _tasks.Remove(task);
        }
    }
}
