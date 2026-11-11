using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionQualityProfileFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _target;
        private QualityProfile _available;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            var first = Db.Insert(new QualityProfile { Name = "Synthetic Default", Items = new(), Language = Language.English });
            var second = Db.Insert(new QualityProfile { Name = "Synthetic Secondary", Items = new(), Language = Language.English });
            _available = Db.Insert(new QualityProfile { Name = "Synthetic Available", Items = new(), Language = Language.English });
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.QualityProfileId = first.Id).With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null).With(m => m.Monitored = true).BuildNew());
            _primary = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-theatrical-imax", Name = "Synthetic Theatrical IMAX", QualityProfileId = first.Id, Monitored = true });
            _target = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D", QualityProfileId = second.Id,
                Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }, LastSearchTime = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc) });
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private MovieEditionQualityProfileChange Change() => new()
        {
            MovieId = _movie.Id, EditionId = _target.Id, QualityProfileId = _available.Id,
            ExpectedMovieMonitored = _movie.Monitored, ExpectedEditionMonitored = _target.Monitored,
            ExpectedMovieFileId = _target.MovieFileId, ExpectedQualityProfileId = _target.QualityProfileId,
            ExpectedName = _target.Name, ExpectedRuleRevision = _target.RuleRevision
        };

        private void Attach(MovieEdition edition)
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = null)
                .With(f => f.RelativePath = "Synthetic-" + edition.Id + "-SyntheticGroup.mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            Subject.SetCurrentFile(_movie.Id, edition.Id, file.Id, null);
            edition.MovieFileId = file.Id;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void changing_primary_or_secondary_profile_preserves_defaults_files_and_other_preferences(bool primary, bool hasFile)
        {
            Attach(_primary);
            if (hasFile) Attach(_target);
            if (primary) Subject.SetPrimaryEdition(_movie.Id, _target.Id);
            using var connection = Db.OpenConnection();
            var parent = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies""");
            var other = Subject.Get(_primary.Id);
            var target = Subject.Get(_target.Id);
            var files = Db.All<MovieFile>();
            Subject.SetQualityProfile(Change()).QualityProfileId.Should().Be(_available.Id);
            target.QualityProfileId = _available.Id;
            Subject.Get(_target.Id).Should().BeEquivalentTo(target);
            Subject.Get(_primary.Id).Should().BeEquivalentTo(other);
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies""")).Should().BeEquivalentTo(parent);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
        }

        [TestCase("parent-monitoring")]
        [TestCase("edition-monitoring")]
        [TestCase("file")]
        [TestCase("profile")]
        [TestCase("revision")]
        [TestCase("name")]
        [TestCase("membership")]
        public void stale_or_foreign_expected_state_is_rejected(string field)
        {
            Attach(_target);
            var change = Change();
            switch (field)
            {
                case "parent-monitoring": change.ExpectedMovieMonitored = false; break;
                case "edition-monitoring": change.ExpectedEditionMonitored = true; break;
                case "file": change.ExpectedMovieFileId = null; break;
                case "profile": change.ExpectedQualityProfileId++; break;
                case "revision": change.ExpectedRuleRevision++; break;
                case "name": change.ExpectedName += " stale"; break;
                case "membership": change.EditionId += 100; break;
            }
            var before = Subject.Get(_target.Id);
            Action assign = () => Subject.SetQualityProfile(change);
            assign.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(before);
        }

        [TestCase(0)]
        [TestCase(999999)]
        public void unavailable_destination_profiles_cannot_replace_the_existing_preference(int profileId)
        {
            var change = Change();
            change.QualityProfileId = profileId;
            Action assign = () => Subject.SetQualityProfile(change);
            if (profileId == 0) assign.Should().Throw<ArgumentException>();
            else assign.Should().Throw<ModelNotFoundException>();
            Subject.Get(_target.Id).QualityProfileId.Should().Be(_target.QualityProfileId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_profiles_can_be_repaired_without_reassigning_or_repairing_file_links(bool brokenFile)
        {
            _target.QualityProfileId = 999999;
            _target.MovieFileId = brokenFile ? 999999 : null;
            Db.Update(_target);
            Subject.SetQualityProfile(Change());
            Subject.Get(_target.Id).QualityProfileId.Should().Be(_available.Id);
            Subject.Get(_target.Id).MovieFileId.Should().Be(_target.MovieFileId);
            Subject.Get(_target.Id).RuleRevision.Should().Be(_target.RuleRevision);
        }

        [Test]
        public void explicit_legacy_default_changes_still_update_only_the_selected_primary()
        {
            Subject.SetPrimaryEdition(_movie.Id, _target.Id);
            Subject.SetQualityProfile(Change());
            _movie.QualityProfileId = _target.QualityProfileId;
            Mocker.Resolve<MovieRepository>().SetFields(_movie, m => m.QualityProfileId);
            Db.Single<Movie>().QualityProfileId.Should().Be(_target.QualityProfileId);
            Subject.Get(_target.Id).QualityProfileId.Should().Be(_target.QualityProfileId);
            Subject.Get(_primary.Id).QualityProfileId.Should().Be(_primary.QualityProfileId);
        }

        [Test]
        public void only_one_concurrent_profile_choice_can_use_the_same_snapshot()
        {
            var first = Change();
            var second = Change();
            second.QualityProfileId = _primary.QualityProfileId;
            var repository = Subject;
            using var start = new ManualResetEventSlim();
            var tasks = new[] { first, second }.Select(change => Task.Run(() =>
            {
                start.Wait();
                try { repository.SetQualityProfile(change); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            start.Set();
            Task.WhenAll(tasks).GetAwaiter().GetResult().Count(success => success).Should().Be(1);
        }

        [Test]
        public void profile_assignment_and_deletion_cannot_commit_a_missing_reference()
        {
            var change = Change();
            var profiles = Mocker.Resolve<QualityProfileRepository>();
            var repository = Subject;
            using var start = new ManualResetEventSlim();
            var assignment = Task.Run(() =>
            {
                start.Wait();
                try { repository.SetQualityProfile(change); return true; }
                catch (ModelNotFoundException) { return false; }
            });
            var deletion = Task.Run(() =>
            {
                start.Wait();
                try { profiles.Delete(_available.Id); return true; }
                catch (QualityProfileInUseException) { return false; }
            });
            start.Set();
            Task.WhenAll(assignment, deletion).GetAwaiter().GetResult();
            (assignment.Result ^ deletion.Result).Should().BeTrue();
            profiles.Exists(_available.Id).Should().Be(assignment.Result);
            Subject.Get(_target.Id).QualityProfileId.Should().Be(assignment.Result ? _available.Id : _target.QualityProfileId);
        }
    }
}
