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
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionPrimarySelectionFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _target;

        [SetUp]
        public void Setup()
        {
            var first = Db.Insert(new QualityProfile { Name = "Synthetic Default", Items = new(), Language = Language.English });
            var second = Db.Insert(new QualityProfile { Name = "Synthetic Secondary", Items = new(), Language = Language.English });
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.QualityProfileId = first.Id)
                .With(m => m.MovieFileId = 0).With(m => m.Monitored = true).With(m => m.PrimaryEditionId = null).BuildNew());
            _primary = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-theatrical-imax", Name = "Synthetic Theatrical IMAX", QualityProfileId = first.Id,
                Monitored = true, Attributes = new() { { "cut", "theatrical" }, { "framing", "imax" } }, LastSearchTime = new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc) });
            _target = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D", QualityProfileId = second.Id,
                Monitored = false, Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }, LastSearchTime = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc) });
            _movie.PrimaryEditionId = _primary.Id;
            _movie.LastSearchTime = _primary.LastSearchTime;
            Db.Update(_movie);
        }

        private MovieFile Attach(MovieEdition edition)
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = null)
                .With(f => f.RelativePath = "Synthetic-" + edition.Id + ".mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            Subject.SetCurrentFile(_movie.Id, edition.Id, file.Id, null);
            return file;
        }

        [Test]
        public void selection_projects_file_and_search_time_without_changing_defaults_preferences_or_files()
        {
            Attach(_primary);
            var targetFile = Attach(_target);
            var editions = Subject.GetByMovie(_movie.Id);
            var files = Db.All<MovieFile>();
            Subject.SelectPrimary(_movie.Id, _target.Id, _primary.Id, targetFile.Id, _target.QualityProfileId);
            var parent = Db.Single<Movie>();
            parent.PrimaryEditionId.Should().Be(_target.Id);
            parent.MovieFileId.Should().Be(targetFile.Id);
            parent.LastSearchTime.Should().Be(_target.LastSearchTime);
            parent.QualityProfileId.Should().Be(_movie.QualityProfileId);
            parent.Monitored.Should().BeTrue();
            Subject.GetByMovie(_movie.Id).Should().BeEquivalentTo(editions);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
        }

        [Test]
        public void stale_metadata_update_after_selection_preserves_the_independent_primary_profile_and_search_date()
        {
            var file = Attach(_target);
            Subject.SelectPrimary(_movie.Id, _target.Id, _primary.Id, file.Id, _target.QualityProfileId);
            _movie.Path += "-SyntheticMoved";
            Mocker.Resolve<MovieRepository>().Update(_movie);
            var parent = Db.Single<Movie>();
            parent.PrimaryEditionId.Should().Be(_target.Id);
            parent.MovieFileId.Should().Be(file.Id);
            parent.QualityProfileId.Should().Be(_movie.QualityProfileId);
            parent.LastSearchTime.Should().Be(_target.LastSearchTime);
            Subject.Get(_target.Id).QualityProfileId.Should().Be(_target.QualityProfileId);
            Subject.Get(_target.Id).LastSearchTime.Should().Be(_target.LastSearchTime);
        }

        [Test]
        public void empty_primary_is_missing_and_clears_the_previous_search_time()
        {
            Attach(_primary);
            _target.LastSearchTime = null;
            Subject.SetFields(_target, e => e.LastSearchTime);
            Subject.SelectPrimary(_movie.Id, _target.Id, _primary.Id, null, _target.QualityProfileId);
            var parent = Db.Single<Movie>();
            parent.MovieFileId.Should().Be(0);
            parent.LastSearchTime.Should().BeNull();
            Db.All<MovieFile>().Should().ContainSingle().Which.MovieEditionId.Should().Be(_primary.Id);
        }

        [TestCase("primary")]
        [TestCase("file")]
        [TestCase("profile")]
        [TestCase("membership")]
        public void stale_or_foreign_selection_rolls_back(string reason)
        {
            var file = Attach(_target);
            var before = Db.Single<Movie>();
            Action select = () => Subject.SelectPrimary(_movie.Id, reason == "membership" ? _target.Id + 100 : _target.Id,
                reason == "primary" ? _primary.Id + 100 : _primary.Id, reason == "file" ? null : file.Id,
                reason == "profile" ? _primary.QualityProfileId : _target.QualityProfileId);
            select.Should().Throw<InvalidOperationException>();
            var parent = Db.Single<Movie>();
            parent.PrimaryEditionId.Should().Be(before.PrimaryEditionId);
            parent.MovieFileId.Should().Be(before.MovieFileId);
            parent.LastSearchTime.Should().Be(before.LastSearchTime);
        }

        [TestCase("unassigned")]
        [TestCase("other-edition")]
        [TestCase("other-movie")]
        [TestCase("missing-file")]
        public void invalid_current_file_ownership_cannot_be_projected(string reason)
        {
            var file = Attach(_target);
            using var connection = Db.OpenConnection();
            if (reason == "missing-file")
            {
                connection.Execute(@"DELETE FROM ""MovieFiles"" WHERE ""Id"" = @Id", new { file.Id });
            }
            else
            {
                connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionId"" = @Owner, ""MovieId"" = @MovieId WHERE ""Id"" = @Id",
                    new { file.Id, Owner = reason == "unassigned" ? (int?)null : reason == "other-edition" ? _primary.Id : _target.Id,
                        MovieId = reason == "other-movie" ? _movie.Id + 100 : _movie.Id });
            }

            Action select = () => Subject.SelectPrimary(_movie.Id, _target.Id, _primary.Id, file.Id, _target.QualityProfileId);
            select.Should().Throw<InvalidOperationException>();
            Db.Single<Movie>().PrimaryEditionId.Should().Be(_primary.Id);
        }

        [Test]
        public void missing_target_profile_cannot_become_primary()
        {
            Db.Delete(Db.All<QualityProfile>().Single(p => p.Id == _target.QualityProfileId));
            Action select = () => Subject.SelectPrimary(_movie.Id, _target.Id, _primary.Id, null, _target.QualityProfileId);
            select.Should().Throw<ModelNotFoundException>();
            Db.Single<Movie>().PrimaryEditionId.Should().Be(_primary.Id);
        }

        [Test]
        public void only_one_concurrent_selection_can_use_the_same_expected_primary()
        {
            var another = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-extra", Name = "Synthetic Extra", QualityProfileId = _target.QualityProfileId });
            var repository = Subject;
            using var start = new ManualResetEventSlim();
            var tasks = new[] { _target, another }.Select(target => Task.Run(() =>
            {
                start.Wait();
                try { repository.SelectPrimary(_movie.Id, target.Id, _primary.Id, null, target.QualityProfileId); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            start.Set();
            Task.WhenAll(tasks).GetAwaiter().GetResult().Count(success => success).Should().Be(1);
            Db.Single<Movie>().MovieFileId.Should().Be(0);
        }
    }
}
