using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Presentation.Mvvm;

namespace SteamCuratorHub.Presentation.ViewModels;

/// <summary>
/// Owns the transient AI-assisted research state for the currently selected game.
/// Every operation is scoped to a context generation so results from a previous
/// game or language can never be applied to the current page.
/// </summary>
public sealed class GameResearchViewModel : ObservableObject, IDisposable
{
    private const int MaxSummaryInputCharacters = 16_000;
    private const int BatchConcurrency = 2;
    private const int MaxContentCacheEntries = 256;
    private static readonly TimeSpan ContentCacheTimeToLive = TimeSpan.FromMinutes(30);

    private readonly ITranslationService _translationService;
    private readonly IAiSummaryService _summaryService;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ContentCacheEntry> _contentCache = new(StringComparer.Ordinal);
    private readonly object _contentCacheGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _contentInflight = new(StringComparer.Ordinal);
    private readonly bool _translationConfigured;
    private readonly bool _summaryConfigured;
    private CancellationTokenSource _contextCancellation = new();
    private Game? _game;
    private string _language = "zh-CN";
    private string _descriptionOriginal = string.Empty;
    private string _descriptionTranslation = string.Empty;
    private string _reviewSummary = string.Empty;
    private string? _errorMessage;
    private string _progressLabel = string.Empty;
    private bool _isShowingOriginalDescription = true;
    private int _generation;
    private int _busyGeneration;
    private int _busyCount;
    private bool _disposed;

    public GameResearchViewModel(ITranslationService translationService, IAiSummaryService summaryService)
        : this(translationService, summaryService, TimeProvider.System)
    {
    }

    public GameResearchViewModel(
        ITranslationService translationService,
        IAiSummaryService summaryService,
        TimeProvider timeProvider)
    {
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _summaryService = summaryService ?? throw new ArgumentNullException(nameof(summaryService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var translationStatus = ReadServiceStatus(translationService);
        var summaryStatus = ReadServiceStatus(summaryService);
        _translationConfigured = translationStatus.IsConfigured;
        _summaryConfigured = summaryStatus.IsConfigured;
        IsConfigured = _translationConfigured && _summaryConfigured;
        ProviderName = MergeStatusValue(translationStatus.ProviderName, summaryStatus.ProviderName);
        Model = MergeStatusValue(translationStatus.Model, summaryStatus.Model);
        UpdateConfigurationLabel();
    }

    public ObservableCollection<ReviewResearchItemViewModel> Reviews { get; } = [];

    public Game? Game => _game;
    public string Language => _language;
    public bool IsConfigured { get; }
    public string ProviderName { get; }
    public string Model { get; }

    public string DescriptionOriginal
    {
        get => _descriptionOriginal;
        private set
        {
            if (SetProperty(ref _descriptionOriginal, value))
                OnPropertyChanged(nameof(DescriptionDisplay));
        }
    }

    public string DescriptionTranslation
    {
        get => _descriptionTranslation;
        private set
        {
            if (!SetProperty(ref _descriptionTranslation, value))
                return;
            OnPropertyChanged(nameof(HasDescriptionTranslation));
            OnPropertyChanged(nameof(DescriptionDisplay));
        }
    }

    public bool HasDescriptionTranslation => !string.IsNullOrWhiteSpace(DescriptionTranslation);

    public bool IsShowingOriginalDescription
    {
        get => _isShowingOriginalDescription;
        private set
        {
            if (SetProperty(ref _isShowingOriginalDescription, value))
                OnPropertyChanged(nameof(DescriptionDisplay));
        }
    }

    public string DescriptionDisplay => IsShowingOriginalDescription || !HasDescriptionTranslation
        ? DescriptionOriginal
        : DescriptionTranslation;

    public string ReviewSummary
    {
        get => _reviewSummary;
        private set
        {
            if (SetProperty(ref _reviewSummary, value))
                OnPropertyChanged(nameof(HasReviewSummary));
        }
    }

    public bool HasReviewSummary => !string.IsNullOrWhiteSpace(ReviewSummary);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public bool IsBusy => _busyGeneration == _generation && _busyCount > 0;
    public bool CanTranslateDescription => _translationConfigured && !IsBusy && !string.IsNullOrWhiteSpace(DescriptionOriginal);
    public bool CanSummarizeReviews => _summaryConfigured && !IsBusy && Reviews.Any(item => !string.IsNullOrWhiteSpace(item.OriginalContent));
    public bool CanTranslateAll => _translationConfigured && !IsBusy && Reviews.Any(item => item.CanTranslate);

    /// <summary>
    /// Selects the game and output language. This immediately cancels every request
    /// belonging to the previous selection and resets all page-scoped output.
    /// </summary>
    public void SetContext(Game? game, string language)
    {
        ThrowIfDisposed();

        var oldCancellation = _contextCancellation;
        _contextCancellation = new CancellationTokenSource();
        oldCancellation.Cancel();
        oldCancellation.Dispose();
        // In-flight work belongs to the previous context. Keep completed content
        // entries reusable, while ensuring a new context never joins a request
        // that is already being canceled for the old game or language.
        _contentInflight.Clear();

        _generation = unchecked(_generation + 1);
        _busyGeneration = _generation;
        _busyCount = 0;
        _game = game;
        _language = NormalizeLanguage(language);
        DescriptionOriginal = SelectDescriptionSource(game, _language);
        DescriptionTranslation = string.Empty;
        IsShowingOriginalDescription = true;
        ReviewSummary = string.Empty;
        ErrorMessage = null;
        Reviews.Clear();

        if (game is not null)
        {
            foreach (var review in game.Reviews)
                Reviews.Add(new ReviewResearchItemViewModel(review, _language, _generation));
        }

        UpdateConfigurationLabel();
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(IsBusy));
        NotifyActionState();
    }

    public async Task TranslateDescriptionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var generation = _generation;
        if (!CanTranslateDescription)
        {
            SetUnavailableMessage(_translationConfigured, generation);
            return;
        }

        var source = DescriptionOriginal;
        BeginOperation(generation, IsChinese ? "正在翻译商店介绍…" : "Translating store description…");
        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            var translated = await GetOrCreateCachedAsync(
                "description-translation",
                source,
                TargetLanguage,
                token => _translationService.TranslateAsync(source, TargetLanguage, token),
                linked.Token);
            if (!IsCurrent(generation))
                return;
            DescriptionTranslation = RequireResult(translated, IsChinese ? "翻译结果为空。" : "The translation was empty.");
            IsShowingOriginalDescription = false;
            ErrorMessage = null;
            ProgressLabel = IsChinese ? "商店介绍翻译完成" : "Store description translated";
        }
        catch (OperationCanceledException) when (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation) && cancellationToken.IsCancellationRequested)
                ProgressLabel = IsChinese ? "已取消" : "Canceled";
        }
        catch (Exception ex)
        {
            SetOperationError(ex, generation);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    public async Task SummarizeReviewsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var generation = _generation;
        if (!CanSummarizeReviews)
        {
            SetUnavailableMessage(_summaryConfigured, generation);
            return;
        }

        var input = BuildReviewSummaryInput(Reviews);
        if (string.IsNullOrWhiteSpace(input))
        {
            ErrorMessage = IsChinese ? "没有可供总结的评论正文。" : "There is no review content to summarize.";
            return;
        }

        BeginOperation(generation, IsChinese ? "正在总结玩家评论…" : "Summarizing player reviews…");
        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            var summary = await GetOrCreateCachedAsync(
                "review-summary",
                input,
                TargetLanguage,
                token => _summaryService.SummarizeAsync(input, TargetLanguage, token),
                linked.Token);
            if (!IsCurrent(generation))
                return;
            ReviewSummary = RequireResult(summary, IsChinese ? "摘要结果为空。" : "The summary was empty.");
            ErrorMessage = null;
            ProgressLabel = IsChinese ? "评论摘要已生成" : "Review summary generated";
        }
        catch (OperationCanceledException) when (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation) && cancellationToken.IsCancellationRequested)
                ProgressLabel = IsChinese ? "已取消" : "Canceled";
        }
        catch (Exception ex)
        {
            SetOperationError(ex, generation);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    public async Task TranslateReviewAsync(ReviewResearchItemViewModel item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        var generation = _generation;
        if (!Owns(item, generation) || !item.CanTranslate || IsBusy)
            return;
        if (!_translationConfigured)
        {
            SetUnavailableMessage(false, generation);
            return;
        }

        BeginOperation(generation, IsChinese ? "正在翻译评论…" : "Translating review…");
        item.SetBusy(true);
        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            await TranslateReviewCoreAsync(item, generation, linked.Token);
            if (!IsCurrent(generation))
                return;
            ErrorMessage = null;
            ProgressLabel = IsChinese ? "评论翻译完成" : "Review translated";
        }
        catch (OperationCanceledException) when (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation) && cancellationToken.IsCancellationRequested)
                ProgressLabel = IsChinese ? "已取消" : "Canceled";
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation))
                item.SetError(SafeError(ex));
            SetOperationError(ex, generation);
        }
        finally
        {
            if (Owns(item, generation))
                item.SetBusy(false);
            EndOperation(generation);
        }
    }

    public async Task TranslateAllReviewsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var generation = _generation;
        if (!CanTranslateAll)
        {
            SetUnavailableMessage(_translationConfigured, generation);
            return;
        }

        var pending = Reviews.Where(item => item.CanTranslate).ToArray();
        BeginOperation(generation, FormatBatchProgress(0, pending.Length));
        var completed = 0;
        var failures = 0;
        string? firstError = null;

        try
        {
            using var linked = CreateLinkedCancellation(cancellationToken);
            for (var offset = 0; offset < pending.Length; offset += BatchConcurrency)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation))
                    return;

                var chunk = pending.Skip(offset).Take(BatchConcurrency).ToArray();
                foreach (var item in chunk)
                    item.SetBusy(true);

                var results = await Task.WhenAll(chunk.Select(async item =>
                {
                    try
                    {
                        await TranslateReviewCoreAsync(item, generation, linked.Token);
                        return (Item: item, Error: (string?)null);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return (Item: item, Error: SafeError(ex));
                    }
                }));

                if (!IsCurrent(generation))
                    return;

                foreach (var result in results)
                {
                    result.Item.SetBusy(false);
                    completed++;
                    if (result.Error is null)
                        continue;
                    failures++;
                    firstError ??= result.Error;
                    result.Item.SetError(result.Error);
                }
                ProgressLabel = FormatBatchProgress(completed, pending.Length);
                NotifyActionState();
            }

            ErrorMessage = failures == 0
                ? null
                : IsChinese
                    ? $"{failures} 条评论翻译失败。{firstError}"
                    : $"{failures} review translation(s) failed. {firstError}";
            ProgressLabel = failures == 0
                ? (IsChinese ? $"已完成 {completed}/{pending.Length}" : $"Completed {completed}/{pending.Length}")
                : (IsChinese ? $"完成 {completed}/{pending.Length} · {failures} 条失败" : $"Completed {completed}/{pending.Length} · {failures} failed");
        }
        catch (OperationCanceledException) when (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation) && cancellationToken.IsCancellationRequested)
                ProgressLabel = IsChinese ? $"已取消 · {completed}/{pending.Length}" : $"Canceled · {completed}/{pending.Length}";
        }
        finally
        {
            if (IsCurrent(generation))
            {
                foreach (var item in pending)
                    item.SetBusy(false);
            }
            EndOperation(generation);
        }
    }

    public void ToggleOriginal(ReviewResearchItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (Owns(item, _generation))
            item.ToggleOriginal();
    }

    public void ToggleExpanded(ReviewResearchItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (Owns(item, _generation))
            item.ToggleExpanded();
    }

    public void ToggleDescriptionOriginal()
    {
        ThrowIfDisposed();
        if (HasDescriptionTranslation)
            IsShowingOriginalDescription = !IsShowingOriginalDescription;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _generation = unchecked(_generation + 1);
        _contextCancellation.Cancel();
        _contextCancellation.Dispose();
        _busyCount = 0;
        lock (_contentCacheGate)
            _contentCache.Clear();
        _contentInflight.Clear();
    }

    private bool IsChinese => _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private string TargetLanguage => IsChinese ? "Chinese (Simplified)" : "English";

    private async Task TranslateReviewCoreAsync(ReviewResearchItemViewModel item, int generation, CancellationToken cancellationToken)
    {
        var source = item.OriginalContent;
        var translated = await GetOrCreateCachedAsync(
            "review-translation",
            source,
            TargetLanguage,
            token => _translationService.TranslateAsync(source, TargetLanguage, token),
            cancellationToken);
        if (!Owns(item, generation))
            return;
        item.SetTranslation(RequireResult(translated, IsChinese ? "翻译结果为空。" : "The translation was empty."));
    }

    private async Task<string> GetOrCreateCachedAsync(
        string operation,
        string source,
        string targetLanguage,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = CreateCacheKey(operation, source, targetLanguage);
        if (TryGetCachedContent(cacheKey, out var cached))
            return cached;

        var operationCancellationToken = _contextCancellation.Token;
        var candidate = new Lazy<Task<string>>(
            () => CreateAndCacheContentAsync(cacheKey, factory, operationCancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var request = _contentInflight.GetOrAdd(cacheKey, candidate);
        Task<string> requestTask;
        try
        {
            requestTask = request.Value;
        }
        catch
        {
            RemoveInFlightContent(cacheKey, request);
            throw;
        }

        _ = requestTask.ContinueWith(
            _ => RemoveInFlightContent(cacheKey, request),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await requestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RemoveInFlightContent(string cacheKey, Lazy<Task<string>> request)
        => ((ICollection<KeyValuePair<string, Lazy<Task<string>>>>)_contentInflight)
            .Remove(new KeyValuePair<string, Lazy<Task<string>>>(cacheKey, request));

    private async Task<string> CreateAndCacheContentAsync(
        string cacheKey,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken cancellationToken)
    {
        // A completed request can populate the cache between the caller's first
        // lookup and publication of this per-key in-flight operation.
        if (TryGetCachedContent(cacheKey, out var cached))
            return cached;

        var result = await factory(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result))
            return result;

        var normalizedResult = result.Trim();
        lock (_contentCacheGate)
        {
            if (_disposed)
                return normalizedResult;

            var now = _timeProvider.GetUtcNow();
            if (_contentCache.TryGetValue(cacheKey, out var existing))
            {
                if (existing.ExpiresAt > now)
                {
                    _contentCache[cacheKey] = existing with { LastAccessAt = now };
                    return existing.Value;
                }

                _contentCache.Remove(cacheKey);
            }

            RemoveExpiredContentCacheEntries(now);
            TrimContentCacheForInsertion();
            _contentCache[cacheKey] = new ContentCacheEntry(
                normalizedResult,
                now.Add(ContentCacheTimeToLive),
                now);
            return normalizedResult;
        }
    }

    private bool TryGetCachedContent(string cacheKey, out string value)
    {
        lock (_contentCacheGate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_contentCache.TryGetValue(cacheKey, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    _contentCache[cacheKey] = entry with { LastAccessAt = now };
                    value = entry.Value;
                    return true;
                }

                _contentCache.Remove(cacheKey);
            }
        }

        value = string.Empty;
        return false;
    }

    private void RemoveExpiredContentCacheEntries(DateTimeOffset now)
    {
        foreach (var cacheKey in _contentCache
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _contentCache.Remove(cacheKey);
        }
    }

    private void TrimContentCacheForInsertion()
    {
        var removeCount = _contentCache.Count - MaxContentCacheEntries + 1;
        if (removeCount <= 0)
            return;

        foreach (var cacheKey in _contentCache
                     .OrderBy(pair => pair.Value.LastAccessAt)
                     .Take(removeCount)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _contentCache.Remove(cacheKey);
        }
    }

    private string CreateCacheKey(string operation, string source, string targetLanguage)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return $"{operation}|{ProviderName}|{Model}|{targetLanguage}|{hash}";
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
        => CancellationTokenSource.CreateLinkedTokenSource(_contextCancellation.Token, cancellationToken);

    private bool IsCurrent(int generation) => !_disposed && generation == _generation;

    private bool Owns(ReviewResearchItemViewModel item, int generation)
        => IsCurrent(generation) && item.ContextGeneration == generation && Reviews.Contains(item);

    private void BeginOperation(int generation, string progress)
    {
        if (!IsCurrent(generation))
            return;
        if (_busyGeneration != generation)
        {
            _busyGeneration = generation;
            _busyCount = 0;
        }
        _busyCount++;
        ErrorMessage = null;
        ProgressLabel = progress;
        OnPropertyChanged(nameof(IsBusy));
        NotifyActionState();
    }

    private void EndOperation(int generation)
    {
        if (!IsCurrent(generation) || _busyGeneration != generation)
            return;
        _busyCount = Math.Max(0, _busyCount - 1);
        OnPropertyChanged(nameof(IsBusy));
        NotifyActionState();
    }

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(CanTranslateDescription));
        OnPropertyChanged(nameof(CanSummarizeReviews));
        OnPropertyChanged(nameof(CanTranslateAll));
    }

    private void SetUnavailableMessage(bool serviceConfigured, int generation)
    {
        if (!IsCurrent(generation))
            return;
        ErrorMessage = serviceConfigured
            ? (IsChinese ? "当前内容不可处理。" : "The current content cannot be processed.")
            : (IsChinese ? "AI 服务尚未配置。" : "The AI service is not configured.");
        UpdateConfigurationLabel();
    }

    private void SetOperationError(Exception exception, int generation)
    {
        if (!IsCurrent(generation))
            return;
        ErrorMessage = SafeError(exception);
        ProgressLabel = IsChinese ? "处理失败" : "Operation failed";
    }

    private void UpdateConfigurationLabel()
    {
        if (_disposed)
            return;
        ProgressLabel = string.Empty;
    }

    private string FormatBatchProgress(int completed, int total)
        => IsChinese ? $"正在翻译 {completed}/{total}" : $"Translating {completed}/{total}";

    private static string RequireResult(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private string SafeError(Exception exception)
    {
        if (exception is not GeminiServiceException geminiException)
            return string.IsNullOrWhiteSpace(exception.Message)
                ? (IsChinese ? "AI 请求失败。" : "AI request failed.")
                : exception.Message;

        return geminiException.Kind switch
        {
            GeminiServiceErrorKind.NotConfigured => IsChinese ? "AI 服务尚未配置。" : "The AI service is not configured.",
            GeminiServiceErrorKind.Timeout => IsChinese ? "AI 请求超时，请重试。" : "The AI request timed out. Try again.",
            GeminiServiceErrorKind.AuthenticationFailed => IsChinese ? "AI API Key 无效或无权访问。" : "The AI API key is invalid or does not have access.",
            GeminiServiceErrorKind.QuotaExceeded => IsChinese ? "AI 额度已用完，请稍后重试。" : "The AI quota has been exceeded. Try again later.",
            GeminiServiceErrorKind.ServiceUnavailable => IsChinese ? "AI 服务暂时不可用，请稍后重试。" : "The AI service is temporarily unavailable. Try again later.",
            GeminiServiceErrorKind.ModelUnavailable => IsChinese ? "当前 AI 模型不可用。" : "The selected AI model is unavailable.",
            GeminiServiceErrorKind.ContentBlocked => IsChinese ? "此内容无法由 AI 处理。" : "The AI service could not process this content.",
            GeminiServiceErrorKind.RequestRejected => IsChinese ? "AI 请求被拒绝，请检查输入后重试。" : "The AI request was rejected. Check the input and try again.",
            GeminiServiceErrorKind.InvalidResponse => IsChinese ? "AI 返回了无效结果，请重试。" : "The AI service returned an invalid result. Try again.",
            _ => IsChinese ? "AI 请求失败。" : "AI request failed."
        };
    }

    private static string NormalizeLanguage(string? language)
        => language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh-CN" : "en-US";

    private static string SelectDescriptionSource(Game? game, string targetLanguage)
    {
        if (game is null)
            return string.Empty;
        if (targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return FirstNonBlank(game.DescriptionEnglish, game.DescriptionChinese);
        return FirstNonBlank(game.DescriptionChinese, game.DescriptionEnglish);
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string BuildReviewSummaryInput(IEnumerable<ReviewResearchItemViewModel> reviews)
    {
        var builder = new StringBuilder(Math.Min(MaxSummaryInputCharacters, 4_096));
        foreach (var review in reviews)
        {
            if (string.IsNullOrWhiteSpace(review.OriginalContent))
                continue;

            var header = $"Recommendation: {(review.Recommended ? "recommended" : "not recommended")}\nSource language: {SanitizeLanguage(review.SourceLanguage)}\nContent:\n";
            const string separator = "\n\n";
            var remaining = MaxSummaryInputCharacters - builder.Length;
            if (remaining <= header.Length + separator.Length)
                break;

            builder.Append(header);
            remaining = MaxSummaryInputCharacters - builder.Length - separator.Length;
            var content = review.OriginalContent.Trim();
            builder.Append(content.AsSpan(0, Math.Min(content.Length, remaining)));
            builder.Append(separator);
            if (builder.Length >= MaxSummaryInputCharacters)
                break;
        }
        return builder.ToString().Trim();
    }

    private static string SanitizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "unknown";
        var sanitized = new string(language.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(32).ToArray());
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }

    private static ServiceStatus ReadServiceStatus(object service)
    {
        if (service is IAiServiceStatus status)
        {
            return new ServiceStatus(
                status.IsConfigured,
                FirstNonBlank(status.ProviderName, TrimServiceSuffix(service.GetType().Name)),
                status.Model?.Trim() ?? string.Empty);
        }

        var type = service.GetType();
        var configured = ReadBooleanProperty(service, type, "IsConfigured") ?? true;
        var provider = ReadStringProperty(service, type, "ProviderName")
                       ?? (type.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini" : TrimServiceSuffix(type.Name));
        var model = ReadStringProperty(service, type, "Model")
                    ?? ReadStringProperty(service, type, "ModelName")
                    ?? string.Empty;
        return new ServiceStatus(configured, provider, model);
    }

    private static bool? ReadBooleanProperty(object instance, Type type, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as bool?;

    private static string? ReadStringProperty(object instance, Type type, string propertyName)
        => type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) as string is { Length: > 0 } value
            ? value.Trim()
            : null;

    private static string TrimServiceSuffix(string name)
        => name.EndsWith("Service", StringComparison.Ordinal) ? name[..^"Service".Length] : name;

    private static string MergeStatusValue(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
            ? first
            : string.Join(" / ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct ServiceStatus(bool IsConfigured, string ProviderName, string Model);
    private readonly record struct ContentCacheEntry(
        string Value,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastAccessAt);
}

/// <summary>Presentation state for one immutable Steam review and its optional translation.</summary>
public sealed class ReviewResearchItemViewModel : ObservableObject
{
    private string _translatedContent;
    private bool _isShowingOriginal = true;
    private bool _isExpanded;
    private bool _isBusy;
    private string? _errorMessage;

    internal ReviewResearchItemViewModel(PlayerReview review, string targetLanguage, int contextGeneration)
    {
        Author = review.Author;
        Recommended = review.Recommended;
        PostedAt = review.PostedAt;
        SourceLanguage = string.IsNullOrWhiteSpace(review.Language) ? "unknown" : review.Language;
        OriginalContent = FirstNonBlank(review.ContentEnglish, review.ContentChinese);
        TargetLanguage = targetLanguage;
        ContextGeneration = contextGeneration;

        _translatedContent = targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(review.ContentChinese)
            && !string.Equals(review.ContentChinese.Trim(), OriginalContent, StringComparison.Ordinal)
                ? review.ContentChinese.Trim()
                : string.Empty;
    }

    public string Author { get; }
    public bool Recommended { get; }
    public DateTime PostedAt { get; }
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }
    public string OriginalContent { get; }
    public string TranslatedContent => _translatedContent;
    internal int ContextGeneration { get; }

    public bool HasTranslation => !string.IsNullOrWhiteSpace(_translatedContent);
    public bool IsShowingOriginal
    {
        get => _isShowingOriginal;
        private set
        {
            if (!SetProperty(ref _isShowingOriginal, value))
                return;
            IsExpanded = false;
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(IsLongContent));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanTranslate));
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string DisplayContent => IsShowingOriginal || !HasTranslation ? OriginalContent : _translatedContent;
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (!SetProperty(ref _isExpanded, value))
                return;
            OnPropertyChanged(nameof(ContentMaxHeight));
            OnPropertyChanged(nameof(ExpandLabel));
        }
    }
    public bool IsLongContent => DisplayContent.Length > 520;
    public double ContentMaxHeight => IsExpanded ? 10_000 : 196;
    public string ExpandLabel => IsExpanded
        ? (IsChinese ? "收起" : "Collapse")
        : (IsChinese ? "展开" : "More");
    public bool CanTranslate => !IsBusy && !HasTranslation && !string.IsNullOrWhiteSpace(OriginalContent) && !IsSourceAlreadyTargetLanguage();
    public bool CanAct => HasTranslation || CanTranslate;
    public string ActionLabel => IsBusy
        ? (IsChinese ? "翻译中…" : "Translating…")
        : HasTranslation
            ? IsShowingOriginal
                ? (IsChinese ? "查看译文" : "Show translation")
                : (IsChinese ? "查看原文" : "Show original")
            : (IsChinese ? "翻译" : "Translate");

    internal void SetTranslation(string value)
    {
        _translatedContent = value;
        _isShowingOriginal = false;
        IsExpanded = false;
        ErrorMessage = null;
        OnPropertyChanged(nameof(TranslatedContent));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(IsShowingOriginal));
        OnPropertyChanged(nameof(DisplayContent));
        OnPropertyChanged(nameof(IsLongContent));
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(ActionLabel));
    }

    internal void SetBusy(bool value) => IsBusy = value;

    internal void SetError(string? value)
    {
        ErrorMessage = value;
        OnPropertyChanged(nameof(ActionLabel));
    }

    internal void ToggleOriginal()
    {
        if (HasTranslation && !IsBusy)
            IsShowingOriginal = !IsShowingOriginal;
    }

    internal void ToggleExpanded()
    {
        if (IsLongContent && !IsBusy)
            IsExpanded = !IsExpanded;
    }

    private bool IsChinese => TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private bool IsSourceAlreadyTargetLanguage()
    {
        var source = SourceLanguage.Trim().ToLowerInvariant();
        return IsChinese
            ? source is "schinese" or "tchinese" or "chinese" or "zh" or "zh-cn" or "zh-tw"
            : source is "english" or "en" or "en-us" or "en-gb";
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
