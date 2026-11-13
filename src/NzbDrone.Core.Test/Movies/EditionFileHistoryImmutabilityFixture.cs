using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionGrabImmutabilityFixture
    {
        private MovieHistory ManualFileHistory(MovieHistoryEventType eventType)
        {
            var row = MovieGrab();
            row.EventType = eventType;
            row.EditionOperationId = null;
            row.DownloadId = null;
            row.Data.Remove(MovieHistory.DOWNLOAD_CLIENT_ID);
            return row;
        }

        [Test]
        public void manual_file_history_identity_survives_every_ordinary_writer(
            [Values(MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.MovieFileRenamed, MovieHistoryEventType.MovieFileDeleted)] MovieHistoryEventType eventType,
            [Values("MovieId", "MovieEditionId", "unassigned", "EditionRuleRevision", "unknown-revision", "EditionOperationId", "DownloadId", "SourceTitle", "Date", "EventType", "client", "client-type", "client-name", "name")] string change,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            RejectMutation(Mocker.Resolve<HistoryRepository>(), ManualFileHistory(eventType), change, writer, "*file history identity*");
        }

        [Test]
        public void manual_file_quality_languages_and_unrelated_notes_remain_editable(
            [Values(MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.MovieFileRenamed, MovieHistoryEventType.MovieFileDeleted)] MovieHistoryEventType eventType,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            var repository = Mocker.Resolve<HistoryRepository>();
            var row = repository.Insert(ManualFileHistory(eventType));
            row.Quality = new QualityModel(Quality.Bluray1080p);
            row.Languages = new() { Language.French };
            row.Data["syntheticNote"] = "Synthetic correction";
            if (writer == "partial") repository.SetFields(row, r => r.Quality, r => r.Languages, r => r.Data);
            else if (writer == "bulk-partial") repository.SetFields(new[] { row }, r => r.Quality, r => r.Languages, r => r.Data);
            else Write(repository, row, nameof(MovieHistory.Data), writer);

            var stored = repository.Get(row.Id);
            stored.MovieEditionId.Should().Be(_edition.Id);
            stored.EditionRuleRevision.Should().Be(1);
            stored.EditionOperationId.Should().BeNull();
            stored.DownloadId.Should().BeNull();
            stored.EventType.Should().Be(eventType);
            stored.Quality.Quality.Should().Be(Quality.Bluray1080p);
            stored.Languages.Should().Equal(Language.French);
            stored.Data["movieEditionName"].Should().Be(_edition.Name);
            stored.Data["syntheticNote"].Should().Be("Synthetic correction");
        }

        [Test]
        public void existing_file_client_correlation_cannot_be_erased_to_make_retirement_eligible(
            [Values("downloadClient", "downloadClientName")] string key,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            var repository = Mocker.Resolve<HistoryRepository>();
            var row = ManualFileHistory(MovieHistoryEventType.MovieFileDeleted);
            row.Data[key] = "Synthetic Saved Client";
            repository.Insert(row);
            row.Data.Remove(key);

            Action update = () => Write(repository, row, nameof(MovieHistory.Data), writer);

            update.Should().Throw<InvalidOperationException>().WithMessage("*file history identity*");
            repository.Get(row.Id).Data[key].Should().Be("Synthetic Saved Client");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void file_history_batch_identity_failure_rolls_back_prior_enrichment(bool partial)
        {
            RejectBatch(Mocker.Resolve<HistoryRepository>(), ManualFileHistory(MovieHistoryEventType.DownloadFolderImported),
                ManualFileHistory(MovieHistoryEventType.MovieFileDeleted), partial, "*file history identity*");
        }

        [Test]
        public void ordinary_update_must_not_reassign_retired_manual_history()
        {
            var editions = Mocker.Resolve<MovieEditionRepository>();
            var target = editions.Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Retired IMAX 3D", EditionKey = "synthetic-retired-imax-3d",
                QualityProfileId = _movie.QualityProfileId, Attributes = new() { { "cut", "theatrical" }, { "framing", "imax" }, { "stereoscopic", "3d" } }
            });
            var row = ManualFileHistory(MovieHistoryEventType.MovieFileDeleted);
            row.MovieEditionId = target.Id;
            row.Data["movieEditionName"] = target.Name;
            var repository = Mocker.Resolve<HistoryRepository>();
            repository.Insert(row);
            editions.RetireEmpty(new MovieEditionRemovalChange
            {
                MovieId = _movie.Id, EditionId = target.Id, ExpectedPrimaryEditionId = _edition.Id,
                ExpectedMovieMonitored = _movie.Monitored, ExpectedEditionMonitored = false, ExpectedMovieFileId = null,
                ExpectedQualityProfileId = target.QualityProfileId, ExpectedRuleRevision = target.RuleRevision, ExpectedName = target.Name
            });
            row.MovieEditionId = _edition.Id;
            row.Data["movieEditionName"] = _edition.Name;

            Action update = () => repository.Update(row);

            update.Should().Throw<InvalidOperationException>().WithMessage("*file history identity*");
            editions.Find(target.Id).Should().BeNull();
            var stored = repository.Get(row.Id);
            stored.MovieEditionId.Should().Be(target.Id);
            stored.Data["movieEditionName"].Should().Be(target.Name);
        }
    }
}
