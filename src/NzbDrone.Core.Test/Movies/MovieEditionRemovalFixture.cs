using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class MovieEditionRemovalFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _kept;
        private MovieEdition _target;

        [SetUp]
        public void Setup()
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Removal Profile", Items = new(), Language = Language.English });
            var metadata = Db.Insert(new MovieMetadata { Title = "Synthetic Lunar Voyage", TmdbId = 80808080, Year = 2026 });
            _movie = Mocker.Resolve<MovieRepository>().Insert(new Movie
            {
                MovieMetadataId = metadata.Id, Path = "/synthetic/lunar-voyage", QualityProfileId = profile.Id, Monitored = true
            });
            _primary = Subject.Get(_movie.PrimaryEditionId.Value);
            _primary.Name = "Synthetic Theatrical IMAX";
            _primary.Attributes = new() { { "cut", "theatrical" }, { "framing", "imax" } };
            Subject.Update(_primary);
            _kept = Subject.Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Directors 3D", EditionKey = "synthetic-directors-3d", QualityProfileId = profile.Id,
                Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }
            });
            _target = Subject.Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Unused Collector", EditionKey = "synthetic-unused-collector", QualityProfileId = profile.Id,
                Attributes = new() { { "cut", "theatrical" }, { "custom-label", "Synthetic Collector" } }, MatchRules = new() { "SyntheticExclusiveGroup$" }
            });
            Attach(_primary);
            Attach(_kept);
            _movie = Mocker.Resolve<MovieRepository>().Get(_movie.Id);
        }

        private void Attach(MovieEdition edition)
        {
            var file = Db.Insert(new MovieFile
            {
                MovieId = _movie.Id, RelativePath = "Synthetic-" + edition.Id + "-SyntheticGroup.mkv", ReleaseGroup = "SyntheticGroup",
                Quality = new QualityModel(Quality.Bluray1080p), Languages = new() { Language.English }, DateAdded = DateTime.UtcNow
            });
            Subject.SetCurrentFile(_movie.Id, edition.Id, file.Id, null);
            edition.MovieFileId = file.Id;
        }

        private MovieEditionRemovalChange Change(MovieEdition target = null)
        {
            target ??= _target;
            return new()
            {
                MovieId = _movie.Id, EditionId = target.Id, ExpectedPrimaryEditionId = _movie.PrimaryEditionId.Value,
                ExpectedMovieMonitored = _movie.Monitored, ExpectedEditionMonitored = target.Monitored,
                ExpectedMovieFileId = target.MovieFileId, ExpectedQualityProfileId = target.QualityProfileId,
                ExpectedRuleRevision = target.RuleRevision, ExpectedName = target.Name
            };
        }

        private void AddReference(string reference)
        {
            if (reference == "pending")
            {
                Mocker.Resolve<PendingReleaseRepository>().Insert(new PendingRelease
                {
                    MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                    Title = "Synthetic.Lunar.Voyage.2026-SyntheticGroup", Added = DateTime.UtcNow,
                    ParsedMovieInfo = new(), Release = new(), AdditionalInfo = new() { EditionName = _target.Name }
                });
            }
            else if (reference.StartsWith("journal"))
            {
                // Fixture history includes both unresolved and completed evidence; neither is unused.
                Db.Insert(new MovieEditionSubmission
                {
                    MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                    OperationId = Guid.NewGuid().ToString("D"), EditionName = _target.Name, EditionAttributes = new(_target.Attributes),
                    QualityProfileId = _target.QualityProfileId, MoviePath = _movie.Path, DownloadClientId = 41, DownloadClientName = "Synthetic Client",
                    Release = new() { Title = "Synthetic.Lunar.Voyage.2026-SyntheticGroup" }, ParsedMovieInfo = new(),
                    State = reference == "journal-settled" ? MovieEditionSubmissionState.Settled : MovieEditionSubmissionState.Prepared,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
            }
            else if (reference.StartsWith("movie-"))
            {
                Mocker.Resolve<HistoryRepository>().Insert(new MovieHistory
                {
                    MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                    EventType = reference == "movie-grab" ? MovieHistoryEventType.Grabbed : MovieHistoryEventType.MovieFileRenamed,
                    Date = DateTime.UtcNow, SourceTitle = "Synthetic.Lunar.Voyage.2026-SyntheticGroup", Quality = new(), Languages = new(),
                    DownloadId = "synthetic-removal", Data = new() { { "movieEditionName", _target.Name }, { MovieHistory.DOWNLOAD_CLIENT_ID, "41" } }
                });
            }
            else
            {
                Mocker.Resolve<DownloadHistoryRepository>().Insert(new DownloadHistory
                {
                    MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                    EventType = reference == "download-grab" ? DownloadHistoryEventType.DownloadGrabbed : DownloadHistoryEventType.DownloadImported,
                    Date = DateTime.UtcNow, SourceTitle = "Synthetic.Lunar.Voyage.2026-SyntheticGroup", DownloadId = "synthetic-removal",
                    DownloadClientId = 41, Data = new() { { "movieEditionName", _target.Name } }
                });
            }
        }

        [Test]
        public void removal_preserves_both_owned_editions_files_and_the_primary_projection()
        {
            using var connection = Db.OpenConnection();
            var movie = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies"" WHERE ""Id"" = @Id", _movie);
            var kept = Subject.GetByMovie(_movie.Id).Where(e => e.Id != _target.Id).ToList();
            var files = Db.All<MovieFile>();

            Subject.RemoveUnused(Change());

            Subject.Find(_target.Id).Should().BeNull();
            Subject.GetByMovie(_movie.Id).Should().BeEquivalentTo(kept);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies"" WHERE ""Id"" = @Id", _movie)).Should().BeEquivalentTo(movie);
        }

        [TestCase("primary", false)]
        [TestCase("primary", true)]
        [TestCase("movie-monitoring", false)]
        [TestCase("movie-monitoring", true)]
        [TestCase("edition-monitoring", false)]
        [TestCase("edition-monitoring", true)]
        [TestCase("file", false)]
        [TestCase("file", true)]
        [TestCase("profile", false)]
        [TestCase("profile", true)]
        [TestCase("revision", false)]
        [TestCase("revision", true)]
        [TestCase("name", false)]
        [TestCase("name", true)]
        [TestCase("other-target", false)]
        [TestCase("other-target", true)]
        public void stale_confirmation_preserves_the_target(string stale, bool retirement)
        {
            var change = Change();
            switch (stale)
            {
                case "primary": change.ExpectedPrimaryEditionId = _kept.Id; break;
                case "movie-monitoring": change.ExpectedMovieMonitored = false; break;
                case "edition-monitoring": change.ExpectedEditionMonitored = true; break;
                case "file": change.ExpectedMovieFileId = _primary.MovieFileId; break;
                case "profile": change.ExpectedQualityProfileId += 100; break;
                case "revision": change.ExpectedRuleRevision++; break;
                case "name": change.ExpectedName += " stale"; break;
                case "other-target": change.EditionId = _kept.Id; break;
            }

            Action remove = () => { if (retirement) Subject.RetireEmpty(change); else Subject.RemoveUnused(change); };
            remove.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(_target);
        }

        [TestCase("primary")]
        [TestCase("monitored")]
        [TestCase("file")]
        [TestCase("retained-file")]
        public void referenced_or_ineligible_targets_are_retained(string reference)
        {
            var target = _target;
            if (reference == "primary") target = _primary;
            else if (reference == "monitored")
            {
                _target.Monitored = true;
                Subject.Update(_target);
            }
            else
            {
                Attach(_target);
                if (reference == "retained-file")
                {
                    using var connection = Db.OpenConnection();
                    connection.Execute(@"UPDATE ""MovieEditions"" SET ""MovieFileId"" = NULL WHERE ""Id"" = @Id", _target);
                    _target.MovieFileId = null;
                }
            }

            var before = Subject.GetByMovie(_movie.Id);
            var files = Db.All<MovieFile>();
            Action remove = () => Subject.RemoveUnused(Change(target));
            remove.Should().Throw<InvalidOperationException>();
            Subject.GetByMovie(_movie.Id).Should().BeEquivalentTo(before);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
        }

        [TestCase("pending")]
        [TestCase("journal-prepared")]
        [TestCase("journal-settled")]
        [TestCase("movie-grab")]
        [TestCase("movie-rename")]
        [TestCase("download-grab")]
        [TestCase("download-terminal")]
        public void any_saved_work_or_history_prevents_unused_removal(string reference)
        {
            AddReference(reference);

            Action remove = () => Subject.RemoveUnused(Change());

            remove.Should().Throw<InvalidOperationException>().WithMessage("*saved files, work or history*");
            Subject.Get(_target.Id).Should().BeEquivalentTo(_target);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_parent_or_target_is_reported_without_removing_other_editions(bool missingParent)
        {
            var change = Change();
            if (missingParent) change.MovieId += 999;
            else change.EditionId += 999;

            Action remove = () => Subject.RemoveUnused(change);

            remove.Should().Throw<ModelNotFoundException>();
            Subject.GetByMovie(_movie.Id).Should().HaveCount(3);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void foreign_membership_or_primary_projection_blocks_removal(bool foreignMembership)
        {
            var metadata = Db.Insert(new MovieMetadata { Title = "Synthetic Solar Voyage", TmdbId = 80808081, Year = 2026 });
            var other = Db.Insert(new Movie
            {
                MovieMetadataId = metadata.Id, Path = "/synthetic/solar-voyage", QualityProfileId = _target.QualityProfileId,
                Monitored = true, PrimaryEditionId = _target.Id
            });
            var change = Change();
            if (foreignMembership)
            {
                change.MovieId = other.Id;
                change.ExpectedPrimaryEditionId = _target.Id;
            }

            Action remove = () => Subject.RemoveUnused(change);

            remove.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(_target);
        }

        [TestCase("pending", false)]
        [TestCase("pending", true)]
        [TestCase("movie-grab", false)]
        [TestCase("movie-grab", true)]
        [TestCase("download-grab", false)]
        [TestCase("download-grab", true)]
        public void concurrent_work_and_removal_leave_one_winner_without_dangling_assignment(string reference, bool retirement)
        {
            if (retirement)
            {
                Db.Insert(ManualHistory());
            }

            var repository = Subject;
            _ = Mocker.Resolve<PendingReleaseRepository>();
            _ = Mocker.Resolve<HistoryRepository>();
            _ = Mocker.Resolve<DownloadHistoryRepository>();
            using var start = new ManualResetEventSlim();
            var remove = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    if (retirement) repository.RetireEmpty(Change());
                    else repository.RemoveUnused(Change());
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            });
            var work = Task.Run(() =>
            {
                start.Wait();
                try { AddReference(reference); return true; }
                catch (InvalidOperationException) { return false; }
            });
            start.Set();
            Task.WaitAll(remove, work);

            new[] { remove.Result, work.Result }.Count(success => success).Should().Be(1);
            (repository.Find(_target.Id) != null).Should().Be(work.Result);
            Mocker.Resolve<MovieRepository>().Get(_movie.Id).MovieFileId.Should().Be(_movie.MovieFileId);
            Db.All<MovieFile>().Should().HaveCount(2);
        }

        [Test]
        public void service_publishes_after_releasing_acquisition_and_file_operation_locks()
        {
            Mocker.SetConstant<IMovieEditionRepository>(Subject);
            Mocker.SetConstant<IMovieRepository>(Mocker.Resolve<MovieRepository>());
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieUpdatedEvent>())).Callback(() =>
            {
                var reentrant = Task.Run(() =>
                {
                    using var acquisition = MovieEditionAcquisitionGate.Acquire(_movie.Id);
                    using var files = MovieFileOperationLock.Acquire(_movie.Id);
                    return Subject.GetByMovie(_movie.Id).Count;
                });
                reentrant.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                reentrant.Result.Should().Be(2);
            });

            Mocker.Resolve<MovieEditionManagementService>().RemoveUnused(Change());

            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<MovieUpdatedEvent>()), Times.Once());
        }
    }
}
