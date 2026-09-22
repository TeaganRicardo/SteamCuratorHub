namespace SteamCuratorHub.Application.Abstractions;
public interface IAiSummaryService { Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken = default); }
