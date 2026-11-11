using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionMonitoringFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _target;

        [SetUp]
        public void Setup()
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Monitoring Profile", Items = new(), Language = Language.English });
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.QualityProfileId = profile.Id).With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null).With(m => m.Monitored = false).BuildNew());
            _primary = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-theatrical-imax", Name = "Synthetic Theatrical IMAX",
                QualityProfileId = profile.Id, Monitored = true, Attributes = new() { { "cut", "theatrical" }, { "framing", "imax" } } });
            _target = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D",
                QualityProfileId = profile.Id, Monitored = false, Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } } });
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private MovieEditionMonitoringChange Change(bool monitored = true) => new()
        {
            MovieId = _movie.Id, EditionId = _target.Id, Monitored = monitored,
            ExpectedMovieMonitored = _movie.Monitored, ExpectedEditionMonitored = _target.Monitored,
            ExpectedMovieFileId = _target.MovieFileId, ExpectedQualityProfileId = _target.QualityProfileId,
            ExpectedName = _target.Name, ExpectedRuleRevision = _target.RuleRevision
        };

        private MovieFile Attach(MovieEdition edition)
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = null)
                .With(f => f.RelativePath = "Synthetic-" + edition.Id + "-SyntheticGroup.mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            Subject.SetCurrentFile(_movie.Id, edition.Id, file.Id, null);
            edition.MovieFileId = file.Id;
            return file;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void enabling_changes_only_the_target_preference_with_or_without_a_file(bool hasFile)
        {
            Attach(_primary);
            if (hasFile) Attach(_target);
            using var connection = Db.OpenConnection();
            var beforeMovie = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies""");
            var beforePrimary = Subject.Get(_primary.Id);
            var beforeTarget = Subject.Get(_target.Id);
            var beforeFiles = Db.All<MovieFile>();
            Subject.SetMonitoring(Change()).Monitored.Should().BeTrue();
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies""")).Should().BeEquivalentTo(beforeMovie);
            Subject.Get(_primary.Id).Should().BeEquivalentTo(beforePrimary);
            beforeTarget.Monitored = true;
            Subject.Get(_target.Id).Should().BeEquivalentTo(beforeTarget);
            Db.All<MovieFile>().Should().BeEquivalentTo(beforeFiles);
        }

        [TestCase("parent-monitoring")]
        [TestCase("edition-monitoring")]
        [TestCase("file")]
        [TestCase("profile")]
        [TestCase("revision")]
        [TestCase("name")]
        [TestCase("foreign-target")]
        public void stale_state_rolls_back(string changed)
        {
            Attach(_target);
            var change = Change();
            switch (changed)
            {
                case "parent-monitoring": change.ExpectedMovieMonitored = true; break;
                case "edition-monitoring": change.ExpectedEditionMonitored = true; break;
                case "file": change.ExpectedMovieFileId = null; break;
                case "profile": change.ExpectedQualityProfileId += 100; break;
                case "revision": change.ExpectedRuleRevision++; break;
                case "name": change.ExpectedName += " stale"; break;
                case "foreign-target": change.EditionId += 100; break;
            }

            var before = Subject.Get(_target.Id);
            Action update = () => Subject.SetMonitoring(change);
            update.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(before);
            Db.Single<Movie>().Monitored.Should().BeFalse();
        }

        [TestCase("missing")]
        [TestCase("unassigned")]
        [TestCase("foreign-movie")]
        [TestCase("foreign-edition")]
        [TestCase("duplicate-pointer")]
        public void invalid_file_links_block_enabling_but_allow_disabling(string invalid)
        {
            var file = Attach(_target);
            if (invalid == "missing") Db.Delete(file);
            else if (invalid == "duplicate-pointer")
            {
                _primary.MovieFileId = file.Id;
                Db.Update(_primary);
            }
            else
            {
                if (invalid == "unassigned") file.MovieEditionId = null;
                if (invalid == "foreign-movie") file.MovieId += 100;
                if (invalid == "foreign-edition") file.MovieEditionId = _primary.Id;
                Db.Update(file);
            }

            Action enable = () => Subject.SetMonitoring(Change());
            enable.Should().Throw<InvalidOperationException>();
            _target.Monitored = true;
            Db.Update(_target);
            Subject.SetMonitoring(Change(false)).Monitored.Should().BeFalse();
            Subject.Get(_target.Id).MovieFileId.Should().Be(file.Id);
        }

        [Test]
        public void absent_profiles_block_enabling_but_allow_disabling()
        {
            _target.QualityProfileId = 999999;
            Db.Update(_target);
            Action enable = () => Subject.SetMonitoring(Change());
            enable.Should().Throw<ModelNotFoundException>();
            _target.Monitored = true;
            Db.Update(_target);
            Subject.SetMonitoring(Change(false)).Monitored.Should().BeFalse();
            Subject.Get(_target.Id).QualityProfileId.Should().Be(999999);
        }

        [Test]
        public void invalid_rules_block_enabling_but_allow_disabling()
        {
            _target.MatchRules = new() { "[" };
            Db.Update(_target);
            Action enable = () => Subject.SetMonitoring(Change());
            enable.Should().Throw<ArgumentException>();
            _target.Monitored = true;
            Db.Update(_target);
            Subject.SetMonitoring(Change(false)).Monitored.Should().BeFalse();
            Subject.Get(_target.Id).MatchRules.Should().Equal("[");
        }

        [Test]
        public void corrupt_zero_values_can_still_be_disabled_with_an_exact_snapshot()
        {
            _target.QualityProfileId = 0;
            _target.MovieFileId = 0;
            _target.RuleRevision = 0;
            _target.Monitored = true;
            Db.Update(_target);
            Subject.SetMonitoring(Change(false)).Monitored.Should().BeFalse();
        }

        [Test]
        public void one_stale_confirmation_cannot_reverse_another_monitoring_change()
        {
            var repository = Subject;
            var first = Change();
            var second = Change();
            using var start = new ManualResetEventSlim();
            var tasks = new[] { first, second }.Select(change => Task.Run(() =>
            {
                start.Wait();
                try { repository.SetMonitoring(change); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            start.Set();
            Task.WhenAll(tasks).GetAwaiter().GetResult().Count(success => success).Should().Be(1);
            Subject.Get(_target.Id).Monitored.Should().BeTrue();
        }

        [Test]
        public void disabling_does_not_remove_pending_work_or_download_history()
        {
            _target.Monitored = true;
            Db.Update(_target);
            var pending = Mocker.Resolve<PendingReleaseRepository>().Insert(new PendingRelease
            {
                MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                Title = "Synthetic.Lunar.Voyage.2026-SyntheticGroup", Added = DateTime.UtcNow,
                ParsedMovieInfo = new ParsedMovieInfo(), Release = new ReleaseInfo(),
                AdditionalInfo = new PendingReleaseAdditionalInfo { EditionName = _target.Name }
            });
            var download = Mocker.Resolve<DownloadHistoryRepository>().Insert(new DownloadHistory
            {
                MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
                DownloadId = "synthetic-monitoring-download", DownloadClientId = 41, Date = DateTime.UtcNow,
                EventType = DownloadHistoryEventType.DownloadGrabbed, Protocol = DownloadProtocol.Usenet,
                SourceTitle = "Synthetic.Lunar.Voyage.2026-SyntheticGroup", Release = new ReleaseInfo(),
                Data = new() { { "movieEditionName", _target.Name } }
            });
            using var connection = Db.OpenConnection();
            var beforePending = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""PendingReleases"" WHERE ""Id""=@Id", new { pending.Id });
            var beforeDownload = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""DownloadHistory"" WHERE ""Id""=@Id", new { download.Id });
            Subject.SetMonitoring(Change(false));
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""PendingReleases"" WHERE ""Id""=@Id", new { pending.Id })).Should().BeEquivalentTo(beforePending);
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""DownloadHistory"" WHERE ""Id""=@Id", new { download.Id })).Should().BeEquivalentTo(beforeDownload);
        }
    }
}
