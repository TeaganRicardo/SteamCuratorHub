using SteamCuratorHub.Application.Services;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.IntegrationTests;

internal static class PublishingIntegrationChecks
{
    public static void Run()
    {
        VerifyContentComposition();
        VerifyTwoPhasePublication();
        VerifyStaleAndExpiredPreparations();
        VerifyCuratorCopyConfirmation();
        PublishingAdvancedIntegrationChecks.Run();
    }

    private static void VerifyContentComposition()
    {
        var group = new CuratorGroupProfile
        {
            Id = "group-a",
            Name = "Group A",
            CuratorIdentifier = "12345",
            FooterEnglish = "Follow Group A for more reviews.",
            FooterChinese = "关注 Group A 查看更多评测。"
        };
        var once = ReviewContentComposer.Compose(
            DraftType.LongReview,
            "en-US",
            "[h1]Verdict[/h1]\r\nRecommended.",
            group);
        Require(once.SteamContent == "[h1]Verdict[/h1]\nRecommended.\n\nFollow Group A for more reviews.",
            "The Curator footer was not composed with normalized Steam formatting.");
        var twice = ReviewContentComposer.Compose(
            DraftType.LongReview,
            "en-US",
            once.SteamContent,
            group);
        Require(twice.SteamContent == once.SteamContent,
            "Applying the same Curator footer twice duplicated it.");

        var emoji = ReviewContentComposer.Compose(DraftType.CuratorShortReview, "zh-CN", "👍🏽 推荐");
        Require(emoji.CharacterCount == 4,
            "Review character counting did not use Unicode text elements.");
        try
        {
            _ = ReviewContentComposer.Compose(
                DraftType.CuratorShortReview,
                "en-US",
                new string('x', ReviewContentComposer.CuratorShortReviewCharacterLimit + 1));
            throw new InvalidOperationException("An over-limit Curator review was accepted.");
        }
        catch (ReviewContentValidationException ex) when (ex.Error == ReviewContentValidationError.TooLong)
        {
        }
    }

    private static void VerifyTwoPhasePublication()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero));
        var workflow = new ReviewPublicationWorkflow(clock);
        var task = CreateTask();
        var preparation = workflow.Prepare(
            task,
            DraftType.LongReview,
            "en-US",
            "[b]Prepared[/b], not yet published.");
        var draft = task.GetDraft(DraftType.LongReview, "en-US")!;
        Require(task.Publications.Count == 0 && !draft.IsPublished,
            "Preparing a Steam form recorded a successful publication prematurely.");
        Require(draft.SteamFormattedContent == preparation.Content.SteamContent,
            "The exact Steam-ready snapshot was not stored on the draft.");

        try
        {
            workflow.Confirm(task, preparation, new Uri("https://example.com/review/1"));
            throw new InvalidOperationException("A non-Steam confirmation URL was accepted.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.InvalidSteamUrl)
        {
        }

        var publication = workflow.Confirm(
            task,
            preparation,
            new Uri("https://steamcommunity.com/id/test/recommended/10/"));
        Require(publication.Succeeded && task.Publications.Count == 1 && draft.IsPublished,
            "Explicit confirmation did not create the publication record.");
        Require(publication.ContentSnapshot == preparation.Content.SteamContent,
            "The confirmed publication snapshot differs from the prepared content.");
        Require(ReferenceEquals(publication, workflow.Confirm(task, preparation)),
            "Repeating confirmation for the same preparation was not idempotent.");

        var repeatedPreparation = workflow.Prepare(
            task,
            DraftType.LongReview,
            "en-US",
            "[b]Prepared[/b], not yet published.");
        var repeatedPublication = workflow.Confirm(task, repeatedPreparation);
        Require(ReferenceEquals(publication, repeatedPublication) && task.Publications.Count == 1,
            "Preparing and confirming unchanged published content created a duplicate record.");
    }

    private static void VerifyStaleAndExpiredPreparations()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.Zero));
        var workflow = new ReviewPublicationWorkflow(clock, TimeSpan.FromMinutes(5));
        var task = CreateTask();
        var stale = workflow.Prepare(task, DraftType.CuratorShortReview, "en-US", "Initial text");
        var draft = task.GetDraft(DraftType.CuratorShortReview, "en-US")!;
        draft.Content = "Changed text";
        draft.SteamFormattedContent = "Changed text";
        draft.Version++;
        try
        {
            workflow.Confirm(task, stale);
            throw new InvalidOperationException("A changed draft was confirmed through a stale preparation.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.StaleDraft)
        {
        }

        var expiring = workflow.Prepare(task, DraftType.CuratorShortReview, "en-US", "Current text");
        clock.Advance(TimeSpan.FromMinutes(6));
        try
        {
            workflow.Confirm(task, expiring);
            throw new InvalidOperationException("An expired publication preparation was confirmed.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.Expired)
        {
        }

        var otherTask = CreateTask(20);
        var crossTask = workflow.Prepare(task, DraftType.LongReview, "en-US", "Task-bound content");
        try
        {
            workflow.Confirm(otherTask, crossTask);
            throw new InvalidOperationException("A publication preparation was confirmed against another task.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.WrongTask)
        {
        }
    }

    private static void VerifyCuratorCopyConfirmation()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero));
        var workflow = new CuratorCopyWorkflow(clock);
        var task = CreateTask();
        var copy = new CuratorCopy { ReviewTaskId = task.Id, Status = "Available" };
        task.CuratorCopies.Add(copy);
        var preparation = workflow.Prepare(task, copy);
        Require(copy.Status == "Available" && copy.ClaimedAt is null,
            "Preparing a Curator Connect claim recorded it prematurely.");
        var confirmed = workflow.Confirm(task, preparation);
        Require(confirmed.Status == "Claimed" && confirmed.ClaimedAt == clock.GetUtcNow().UtcDateTime,
            "Explicit Curator Connect confirmation was not recorded.");
        Require(ReferenceEquals(confirmed, workflow.Confirm(task, preparation)),
            "Repeating a Curator Connect confirmation was not idempotent.");
    }

    private static ReviewTask CreateTask(uint appId = 10) => new()
    {
        Game = new Game(appId, $"Game {appId}", $"游戏 {appId}"),
        TargetHours = 4,
        DueDate = DateTime.Today.AddDays(7),
        Status = ReviewTaskStatus.ReadyToPublish
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
