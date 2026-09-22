using System.Text.Json;
using Microsoft.Data.Sqlite;
using SteamCuratorHub.Application.Services;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Infrastructure.Services;

namespace SteamCuratorHub.IntegrationTests;

internal static class PublishingAdvancedIntegrationChecks
{
    public static void Run()
    {
        VerifyRecommendationStateAndAppBinding();
        VerifyAutomationScripts();
        VerifyCuratorSettings();
        VerifyOfferImportAndPersistence();
        VerifyLegacySchemaMigration();
    }

    private static void VerifyRecommendationStateAndAppBinding()
    {
        var workflow = new ReviewPublicationWorkflow();
        var task = CreateTask();
        var recommended = workflow.Prepare(
            task,
            DraftType.CuratorShortReview,
            "en-US",
            "Same review text",
            recommendationState: CuratorRecommendationState.Recommended);
        var notRecommended = workflow.Prepare(
            task,
            DraftType.CuratorShortReview,
            "en-US",
            "Same review text",
            recommendationState: CuratorRecommendationState.NotRecommended);
        Require(recommended.Content.ContentHash != notRecommended.Content.ContentHash,
            "Curator recommendation state was not included in the publication fingerprint.");
        try
        {
            workflow.Confirm(task, recommended);
            throw new InvalidOperationException("A recommendation-state change did not invalidate the previous preparation.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.StaleDraft)
        {
        }

        var publication = workflow.Confirm(task, notRecommended);
        Require(publication.RecommendationState == CuratorRecommendationState.NotRecommended,
            "The confirmed Curator recommendation state was not recorded.");

        var appBoundTask = CreateTask();
        var appBoundPreparation = workflow.Prepare(
            appBoundTask,
            DraftType.LongReview,
            "en-US",
            "Prepared for app 10");
        appBoundTask.Game = new Game(20, "Replacement game", "替换游戏");
        try
        {
            workflow.Confirm(appBoundTask, appBoundPreparation);
            throw new InvalidOperationException("A preparation was confirmed after its task AppID changed.");
        }
        catch (PublicationConfirmationException ex) when (ex.Error == PublicationConfirmationError.WrongApp)
        {
        }
    }

    private static void VerifyAutomationScripts()
    {
        const string content = "safe text\n\"; document.body.innerHTML = 'unsafe'; //";
        var longScript = SteamPublishingAutomationScripts.BuildLongReviewFillScript(10, content);
        Require(longScript.Contains(JsonSerializer.Serialize(content), StringComparison.Ordinal),
            "Long-review content was not JSON-encoded into the automation script.");
        Require(!longScript.Contains(".click(", StringComparison.Ordinal)
                && !longScript.Contains(".submit(", StringComparison.Ordinal)
                && !longScript.Contains("requestSubmit", StringComparison.Ordinal),
            "The safe form-preparation script contains a submission action.");

        var recommendedScript = SteamPublishingAutomationScripts.BuildCuratorRecommendationFillScript(
            "12345", 10, "review", CuratorRecommendationState.Recommended);
        var rejectedScript = SteamPublishingAutomationScripts.BuildCuratorRecommendationFillScript(
            "12345", 10, "review", CuratorRecommendationState.NotRecommended);
        var informationalScript = SteamPublishingAutomationScripts.BuildCuratorRecommendationFillScript(
            "12345", 10, "review", CuratorRecommendationState.Informational);
        Require(recommendedScript.Contains("const recommendationState = \"0\"", StringComparison.Ordinal)
                && rejectedScript.Contains("const recommendationState = \"1\"", StringComparison.Ordinal)
                && informationalScript.Contains("const recommendationState = \"2\"", StringComparison.Ordinal),
            "Curator recommendation states were not mapped to distinct Steam form values.");

        var discoveryScript = SteamPublishingAutomationScripts.BuildCuratorConnectDiscoveryScript("12345");
        Require(!discoveryScript.Contains(".click(", StringComparison.Ordinal)
                && !discoveryScript.Contains(".submit(", StringComparison.Ordinal),
            "Curator Connect discovery performs a state-changing DOM action.");
        var parsed = SteamPublishingAutomationScripts.ParseCuratorOffers("""
            {
              "success": true,
              "code": "ready",
              "offers": [
                { "offerId": "offer_1", "appId": "10", "gameName": "Game 10", "status": "Available", "expiresAt": "2026-10-01T00:00:00Z" },
                { "offerId": "offer_1", "appId": "10", "gameName": "Duplicate", "status": "Available", "expiresAt": "" },
                { "offerId": "../bad", "appId": "20", "gameName": "Unsafe", "status": "Available", "expiresAt": "" }
              ]
            }
            """);
        Require(parsed.Success && parsed.Offers.Count == 1
                               && parsed.Offers[0].SteamOfferId == "offer_1"
                               && parsed.Offers[0].AppId == 10,
            "Curator Connect offer parsing did not validate and deduplicate page data.");
    }

    private static void VerifyCuratorSettings()
    {
        var directory = Directory.CreateTempSubdirectory("SteamCuratorHub-CuratorSettings-");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, "curator.json");
            var service = new JsonCuratorPublishingSettingsService(path);
            var group = new CuratorGroupProfile
            {
                Id = "primary",
                Name = "Primary group",
                CuratorIdentifier = "12345",
                FooterEnglish = "English footer",
                FooterChinese = "中文组尾"
            };
            service.Save(new CuratorPublishingSettings
            {
                CuratorIdentifier = " 12345 ",
                SelectedGroupId = group.Id,
                Groups = [group]
            });
            var restored = service.Load();
            Require(restored.CuratorIdentifier == "12345"
                    && restored.SelectedGroup?.Id == group.Id
                    && restored.SelectedGroup.GetFooter("zh-CN") == group.FooterChinese,
                "Curator identifier or group footer settings were not restored.");
            Require(!File.ReadAllText(path).Contains("cookie", StringComparison.OrdinalIgnoreCase)
                    && !File.ReadAllText(path).Contains("api_key", StringComparison.OrdinalIgnoreCase),
                "Curator publishing settings contain a credential field.");

            var grapheme = "👍🏽";
            var exactLimit = string.Concat(Enumerable.Repeat(grapheme, ReviewContentComposer.LongReviewCharacterLimit));
            service.Save(new CuratorPublishingSettings
            {
                Groups = [group with { FooterEnglish = exactLimit }]
            });
            Require(service.Load().Groups.Single().FooterEnglish == exactLimit,
                "A valid footer at the Unicode text-element limit was changed.");
            try
            {
                service.Save(new CuratorPublishingSettings
                {
                    Groups = [group with { FooterEnglish = exactLimit + grapheme }]
                });
                throw new InvalidOperationException("An over-limit Curator footer was silently truncated.");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void VerifyOfferImportAndPersistence()
    {
        var directory = Directory.CreateTempSubdirectory("SteamCuratorHub-CuratorCopy-");
        try
        {
            var task = CreateTask();
            var importer = new CuratorCopyImportService();
            var expiry = DateTime.UtcNow.AddDays(4);
            var first = importer.Import(task,
            [
                new CuratorConnectOffer("offer_10", 10, "Game 10", CuratorCopyStatus.Available, expiry),
                new CuratorConnectOffer("offer_10", 10, "Duplicate", CuratorCopyStatus.Available, expiry),
                new CuratorConnectOffer("offer_20", 20, "Wrong app", CuratorCopyStatus.Available, expiry)
            ]);
            Require(first.Added.Count == 1 && first.IgnoredCount == 2 && task.CuratorCopies.Count == 1,
                "Curator Connect offers were not safely matched to the task AppID and offer ID.");
            var copy = task.CuratorCopies.Single();
            var claimWorkflow = new CuratorCopyWorkflow();
            var claim = claimWorkflow.Prepare(task, copy);
            Require(claim.CanLocateOnSteam && claim.SteamOfferId == "offer_10" && claim.AppId == 10,
                "The Curator claim preparation is not bound to its Steam offer and AppID.");
            claimWorkflow.Confirm(task, claim);
            importer.Import(task,
            [
                new CuratorConnectOffer("offer_10", 10, "Renamed game", CuratorCopyStatus.Available, expiry)
            ]);
            Require(copy.State == CuratorCopyStatus.Claimed && copy.GameName == "Renamed game",
                "Refreshing Curator offers rolled back a confirmed claim or failed to update metadata.");

            var databasePath = System.IO.Path.Combine(directory.FullName, "publishing.db");
            var repository = new SqliteReviewTaskRepository(databasePath);
            var publicationWorkflow = new ReviewPublicationWorkflow();
            var preparation = publicationWorkflow.Prepare(
                task,
                DraftType.CuratorShortReview,
                "en-US",
                "Informational note",
                recommendationState: CuratorRecommendationState.Informational);
            publicationWorkflow.Confirm(task, preparation);
            repository.Add(task);
            var restored = repository.GetById(task.Id)!;
            var restoredCopy = restored.CuratorCopies.Single();
            Require(restoredCopy.SteamOfferId == copy.SteamOfferId
                    && restoredCopy.AppId == copy.AppId
                    && restoredCopy.GameName == copy.GameName
                    && restoredCopy.ExpiresAt.HasValue
                    && restoredCopy.State == CuratorCopyStatus.Claimed,
                "Curator Connect identity and lifecycle fields were not persisted.");
            Require(restored.Drafts.Single().RecommendationState == CuratorRecommendationState.Informational
                    && restored.Publications.Single().RecommendationState == CuratorRecommendationState.Informational,
                "Curator recommendation state was not persisted with its draft and publication.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static void VerifyLegacySchemaMigration()
    {
        var directory = Directory.CreateTempSubdirectory("SteamCuratorHub-PublishingMigration-");
        try
        {
            var databasePath = System.IO.Path.Combine(directory.FullName, "legacy.db");
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Drafts (
                        Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL, Type INTEGER NOT NULL,
                        Language TEXT NOT NULL, Content TEXT NOT NULL, SteamFormattedContent TEXT NULL,
                        Version INTEGER NOT NULL, IsPublished INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    CREATE TABLE Publications (
                        Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL, Type INTEGER NOT NULL,
                        PublishedAt TEXT NOT NULL, Succeeded INTEGER NOT NULL, SteamUrl TEXT NULL,
                        ContentSnapshot TEXT NULL, ErrorMessage TEXT NULL);
                    CREATE TABLE CuratorCopies (
                        Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL, Status TEXT NOT NULL,
                        ClaimedAt TEXT NULL, Notes TEXT NULL);
                    """;
                command.ExecuteNonQuery();
            }
            _ = new SqliteReviewTaskRepository(databasePath);
            using var migrated = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            migrated.Open();
            Require(HasColumn(migrated, "Drafts", "RecommendationState")
                    && HasColumn(migrated, "Publications", "RecommendationState")
                    && HasColumn(migrated, "CuratorCopies", "SteamOfferId")
                    && HasColumn(migrated, "CuratorCopies", "AppId")
                    && HasColumn(migrated, "CuratorCopies", "ExpiresAt"),
                "Legacy publication tables were not migrated safely.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static ReviewTask CreateTask() => new()
    {
        Game = new Game(10, "Game 10", "游戏 10"),
        TargetHours = 4,
        DueDate = DateTime.Today.AddDays(7),
        Status = ReviewTaskStatus.ReadyToPublish
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
