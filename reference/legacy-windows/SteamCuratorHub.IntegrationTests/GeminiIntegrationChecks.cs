using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;
using SteamCuratorHub.Presentation.ViewModels;

internal static class GeminiIntegrationChecks
{
    public static async Task RunAsync()
    {
        VerifyEnvironmentConfiguration();
        await VerifyTransportAsync();
        await VerifyResearchWorkflowAsync();
        await VerifyStaleResultIsolationAsync();
        await VerifyLocalizedFailureAsync();
        Console.WriteLine("gemini transport: ok (configuration, header isolation, retry, cancellation, response mapping)");
        Console.WriteLine("AI research: ok (real review bodies, privacy, cache, batch translation, context isolation)");
    }

    private static void VerifyEnvironmentConfiguration()
    {
        var variableNames = new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY", "GEMINI_MODEL", "GEMINI_API_ENDPOINT" };
        var originalValues = variableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        const string GoogleKey = "google-test-key";
        const string GeminiKey = "gemini-test-key";
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", GoogleKey);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", GeminiKey);
            Environment.SetEnvironmentVariable("GEMINI_MODEL", null);
            Environment.SetEnvironmentVariable("GEMINI_API_ENDPOINT", "https://credentials.example.invalid/");

            var options = GeminiOptions.FromEnvironment();
            Check(options.ApiKey == GoogleKey, "GOOGLE_API_KEY did not take precedence over GEMINI_API_KEY.");
            Check(options.Model == GeminiOptions.DefaultModel, "Gemini did not select the current default model.");
            Check(options.Endpoint == GeminiOptions.DefaultEndpoint, "An environment variable redirected the production Gemini endpoint.");
            Check(!options.ToString().Contains(GoogleKey, StringComparison.Ordinal), "GeminiOptions.ToString exposed the API key.");
        }
        finally
        {
            foreach (var pair in originalValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static async Task VerifyTransportAsync()
    {
        const string ApiKey = "transport-test-key";
        var successHandler = new RecordingGeminiHandler((attempt, _, _) => Task.FromResult(JsonResponse("""
            {
              "candidates": [
                {
                  "finishReason": "STOP",
                  "content": { "parts": [ { "text": "first " }, { "text": "second" } ] }
                }
              ]
            }
            """)));
        using (var httpClient = new HttpClient(successHandler))
        using (var service = CreateService(httpClient, ApiKey, maxInputCharacters: 8))
        {
            var output = await service.TranslateAsync(new string('X', 32), "Chinese (Simplified)");
            Check(output == "first second", "Gemini multi-part output was not combined.");
            Check(successHandler.Requests.Count == 1, "A successful Gemini request was duplicated.");
            var request = successHandler.Requests.Single();
            Check(request.RequestUri.AbsolutePath.EndsWith($"models/{GeminiOptions.DefaultModel}:generateContent", StringComparison.Ordinal),
                "The Gemini request did not use the current default model path.");
            Check(request.ApiKeyHeader == ApiKey, "The Gemini API key was not sent in x-goog-api-key.");
            Check(!request.RequestUri.AbsoluteUri.Contains(ApiKey, StringComparison.Ordinal), "The Gemini request URI exposed the API key.");
            Check(request.Body.Contains("systemInstruction", StringComparison.Ordinal), "The Gemini request did not isolate untrusted source content.");
            Check(request.Body.Contains("SOURCE_TEXT_BEGIN\\nXXXXXXXX\\nSOURCE_TEXT_END", StringComparison.Ordinal),
                "The Gemini source-text bound was not applied before prompt construction.");
            Check(!request.Body.Contains(new string('X', 9), StringComparison.Ordinal), "Gemini received source text beyond the configured input bound.");
        }

        var retryHandler = new RecordingGeminiHandler((attempt, _, _) =>
        {
            if (attempt < 3)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return Task.FromResult(response);
            }

            return Task.FromResult(JsonResponse("""
                { "candidates": [ { "finishReason": "STOP", "content": { "parts": [ { "text": "recovered" } ] } } ] }
                """));
        });
        using (var httpClient = new HttpClient(retryHandler))
        using (var service = CreateService(httpClient, ApiKey))
        {
            Check(await service.SummarizeAsync("review body", "English") == "recovered", "Gemini did not recover after transient rate limits.");
            Check(retryHandler.Requests.Count == 3, "Gemini did not apply the bounded three-attempt retry policy.");
        }

        var authenticationHandler = new RecordingGeminiHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using (var httpClient = new HttpClient(authenticationHandler))
        using (var service = CreateService(httpClient, ApiKey))
        {
            var error = await CaptureGeminiErrorAsync(() => service.TranslateAsync("text", "English"));
            Check(error.Kind == GeminiServiceErrorKind.AuthenticationFailed, "Gemini authentication errors were not categorized.");
            Check(authenticationHandler.Requests.Count == 1, "A non-transient Gemini authentication failure was retried.");
            Check(!error.ToString().Contains(ApiKey, StringComparison.Ordinal), "A Gemini exception exposed the API key.");
        }

        var blockedHandler = new RecordingGeminiHandler((_, _, _) => Task.FromResult(JsonResponse("""
            { "promptFeedback": { "blockReason": "SAFETY" } }
            """)));
        using (var httpClient = new HttpClient(blockedHandler))
        using (var service = CreateService(httpClient, ApiKey))
        {
            var error = await CaptureGeminiErrorAsync(() => service.SummarizeAsync("text", "English"));
            Check(error.Kind == GeminiServiceErrorKind.ContentBlocked, "Gemini safety blocking was not categorized.");
        }

        var timeoutHandler = new RecordingGeminiHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return JsonResponse("{}");
        });
        using (var httpClient = new HttpClient(timeoutHandler))
        using (var service = CreateService(httpClient, ApiKey, timeout: TimeSpan.FromMilliseconds(20)))
        {
            var error = await CaptureGeminiErrorAsync(() => service.TranslateAsync("text", "English"));
            Check(error.Kind == GeminiServiceErrorKind.Timeout, "Gemini operation timeout was not distinguished from caller cancellation.");
        }

        var cancellationHandler = new RecordingGeminiHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return JsonResponse("{}");
        });
        using (var httpClient = new HttpClient(cancellationHandler))
        using (var service = CreateService(httpClient, ApiKey, timeout: TimeSpan.FromSeconds(2)))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
        {
            try
            {
                await service.TranslateAsync("text", "English", cancellation.Token);
                throw new InvalidOperationException("Caller cancellation did not stop the Gemini request.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        var noKeyHandler = new RecordingGeminiHandler((_, _, _) => Task.FromResult(JsonResponse("{}")));
        using (var httpClient = new HttpClient(noKeyHandler))
        using (var service = CreateService(httpClient, apiKey: null))
        {
            var error = await CaptureGeminiErrorAsync(() => service.TranslateAsync("text", "English"));
            Check(error.Kind == GeminiServiceErrorKind.NotConfigured, "Missing Gemini configuration was not surfaced.");
            Check(noKeyHandler.Requests.Count == 0, "An unconfigured Gemini service attempted a network request.");
        }
    }

    private static async Task VerifyResearchWorkflowAsync()
    {
        var ai = new RecordingAiService();
        using var research = new GameResearchViewModel(ai, ai);
        var game = CreateResearchGame(10, "Original store description");
        research.SetContext(game, "zh-CN");

        Check(research.IsConfigured && research.Model == "test-model", "The research workspace did not expose AI service status.");
        Check(research.Reviews.Count == 3, "The research workspace did not map real reviews.");
        Check(research.Reviews[0].DisplayContent == "Excellent combat and pacing.", "The original review content was not preserved.");

        await research.TranslateDescriptionAsync();
        await research.TranslateDescriptionAsync();
        Check(ai.TranslationInputs.Count(input => input.Text == "Original store description") == 1,
            "Description translation did not use the content cache.");
        Check(research.DescriptionTranslation.Contains("Chinese (Simplified)", StringComparison.Ordinal),
            "Description translation was not stored separately.");

        await research.SummarizeReviewsAsync();
        Check(ai.SummaryInputs.Count == 1, "Review summary did not call the AI service exactly once.");
        var summaryInput = ai.SummaryInputs.Single().Text;
        Check(summaryInput.Contains("Excellent combat and pacing.", StringComparison.Ordinal)
              && summaryInput.Contains("The tutorial is unclear.", StringComparison.Ordinal),
            "Review summary input did not contain real review bodies.");
        Check(!summaryInput.Contains("SECRET_AUTHOR_7656119", StringComparison.Ordinal),
            "Review summary input exposed author or SteamID metadata.");
        Check(research.HasReviewSummary, "The review summary result was not exposed to the UI.");

        await research.TranslateAllReviewsAsync();
        Check(research.Reviews[0].HasTranslation && research.Reviews[1].HasTranslation,
            "Batch translation did not map eligible review results.");
        Check(!research.Reviews[2].HasTranslation,
            "Batch translation processed a review already written in the target language.");
        Check(research.Reviews[0].OriginalContent == "Excellent combat and pacing.",
            "Batch translation overwrote original review content.");
        var translatedCallCount = ai.TranslationInputs.Count;
        await research.TranslateAllReviewsAsync();
        Check(ai.TranslationInputs.Count == translatedCallCount,
            "Batch translation reprocessed reviews that already had translations.");

        var firstReview = research.Reviews[0];
        Check(!firstReview.IsShowingOriginal, "A translated review did not show its translation.");
        research.ToggleOriginal(firstReview);
        Check(firstReview.IsShowingOriginal && firstReview.DisplayContent == firstReview.OriginalContent,
            "The review original/translation toggle did not restore the source text.");
    }

    private static async Task VerifyStaleResultIsolationAsync()
    {
        var ai = new DeferredAiService();
        using var research = new GameResearchViewModel(ai, ai);
        research.SetContext(CreateResearchGame(20, "First description"), "zh-CN");
        var pending = research.TranslateDescriptionAsync();
        await ai.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        research.SetContext(CreateResearchGame(30, "Second description"), "en-US");
        ai.Complete("late translation");
        await pending;

        Check(research.Game?.AppId == 30 && research.Language == "en-US", "The research context did not switch games and language.");
        Check(string.IsNullOrEmpty(research.DescriptionTranslation), "A stale translation was written into the new game context.");
        Check(!research.IsBusy, "A canceled previous context left the new research workspace busy.");
    }

    private static async Task VerifyLocalizedFailureAsync()
    {
        var ai = new ThrowingAiService();
        using var research = new GameResearchViewModel(ai, ai);
        research.SetContext(CreateResearchGame(40, "Description"), "zh-CN");
        await research.TranslateDescriptionAsync();
        Check(research.ErrorMessage?.Contains("额度", StringComparison.Ordinal) == true,
            "A Gemini quota failure was not mapped to a safe Chinese message.");
        Check(!research.ErrorMessage!.Contains("transport-internal-detail", StringComparison.Ordinal),
            "The research UI exposed the underlying Gemini exception message.");
    }

    private static Game CreateResearchGame(uint appId, string descriptionEnglish)
        => new(appId, $"Game {appId}", $"游戏 {appId}")
        {
            DescriptionEnglish = descriptionEnglish,
            DescriptionChinese = "中文商店介绍",
            Reviews =
            [
                new PlayerReview
                {
                    Author = "SECRET_AUTHOR_7656119",
                    ContentEnglish = "Excellent combat and pacing.",
                    Language = "english",
                    Recommended = true,
                },
                new PlayerReview
                {
                    Author = "Second author",
                    ContentEnglish = "The tutorial is unclear.",
                    Language = "french",
                    Recommended = false,
                },
                new PlayerReview
                {
                    Author = "Chinese author",
                    ContentEnglish = "剧情很有趣。",
                    Language = "schinese",
                    Recommended = true,
                },
            ],
        };

    private static FreeGeminiService CreateService(
        HttpClient httpClient,
        string? apiKey,
        int maxInputCharacters = 20_000,
        TimeSpan? timeout = null)
        => new(httpClient, new GeminiOptions
        {
            ApiKey = apiKey,
            Model = GeminiOptions.DefaultModel,
            Endpoint = new Uri("https://gemini.test/v1beta/"),
            MaxInputCharacters = maxInputCharacters,
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
        });

    private static async Task<GeminiServiceException> CaptureGeminiErrorAsync(Func<Task<string>> action)
    {
        try
        {
            _ = await action();
        }
        catch (GeminiServiceException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The Gemini operation did not report an expected error.");
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record GeminiRequestSnapshot(Uri RequestUri, string? ApiKeyHeader, string Body);

    private sealed class RecordingGeminiHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _attempt;
        public List<GeminiRequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.SingleOrDefault()
                : null;
            Requests.Add(new GeminiRequestSnapshot(
                request.RequestUri ?? throw new InvalidOperationException("Gemini test request had no URI."),
                apiKey,
                body));
            return await responder(Interlocked.Increment(ref _attempt), request, cancellationToken);
        }
    }

    private sealed class RecordingAiService : ITranslationService, IAiSummaryService, IAiServiceStatus
    {
        public List<(string Text, string Language)> TranslationInputs { get; } = [];
        public List<(string Text, string Language)> SummaryInputs { get; } = [];
        public bool IsConfigured => true;
        public string ProviderName => "Test AI";
        public string Model => "test-model";

        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslationInputs.Add((text, targetLanguage));
            return Task.FromResult($"[{targetLanguage}] {text}");
        }

        public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryInputs.Add((text, language));
            return Task.FromResult($"[{language}] summary");
        }
    }

    private sealed class DeferredAiService : ITranslationService, IAiSummaryService, IAiServiceStatus
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConfigured => true;
        public string ProviderName => "Deferred AI";
        public string Model => "deferred-model";

        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult();
            return _completion.Task;
        }

        public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken = default)
            => Task.FromResult("summary");

        public void Complete(string result) => _completion.TrySetResult(result);
    }

    private sealed class ThrowingAiService : ITranslationService, IAiSummaryService, IAiServiceStatus
    {
        public bool IsConfigured => true;
        public string ProviderName => "Throwing AI";
        public string Model => "error-model";

        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new GeminiServiceException(
                GeminiServiceErrorKind.QuotaExceeded,
                "transport-internal-detail"));

        public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken = default)
            => TranslateAsync(text, language, cancellationToken);
    }
}
