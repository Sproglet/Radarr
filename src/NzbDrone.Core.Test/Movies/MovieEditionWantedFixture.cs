using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionWantedFixture : DbTest<MovieRepository, Movie>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _secondary;
        private QualityProfile _profile;
        private QualityProfile _secondaryProfile;
        private int _number;

        [SetUp]
        public void Setup()
        {
            _number = 0;
            _profile = Db.Insert(new QualityProfile { Name = "Synthetic Primary Profile", Items = new(), Language = Language.English });
            _secondaryProfile = Db.Insert(new QualityProfile { Name = "Synthetic Secondary Profile", Items = new(), Language = Language.English });
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            Mocker.SetConstant<IAlternativeTitleRepository>(Mocker.Resolve<AlternativeTitleRepository>());
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            _movie = NewMovie();
            _primary = Mocker.Resolve<MovieEditionRepository>().GetByMovie(_movie.Id).Single();
            _secondary = Db.Insert(new MovieEdition
            {
                MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D",
                QualityProfileId = _secondaryProfile.Id, Monitored = true,
                Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }
            });
        }

        private Movie NewMovie()
        {
            var number = ++_number;
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew()
                .With(m => m.TmdbId = 900000 + number).With(m => m.Title = "Synthetic Wanted " + number)
                .With(m => m.SortTitle = "syntheticwanted" + number).With(m => m.Year = 2026).BuildNew());
            return Subject.Insert(Builder<Movie>.CreateNew().With(m => m.MovieMetadataId = metadata.Id)
                .With(m => m.QualityProfileId = _profile.Id).With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null).With(m => m.Monitored = true).BuildNew());
        }

        private MovieFile Attach(MovieEdition edition, Quality quality)
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = edition.MovieId)
                .With(f => f.MovieEditionId = null).With(f => f.Quality = new QualityModel(quality))
                .With(f => f.Languages = new List<Language> { Language.English }).With(f => f.ReleaseGroup = "SyntheticGroup").BuildNew());
            Mocker.Resolve<MovieEditionRepository>().SetCurrentFile(edition.MovieId, edition.Id, file.Id, null);
            return file;
        }

        private PagingSpec<Movie> Page(bool monitored = true, int page = 1, int size = 10)
        {
            return new PagingSpec<Movie>
            {
                Page = page, PageSize = size, SortKey = "MovieMetadata.SortTitle", SortDirection = SortDirection.Ascending,
                FilterExpressions = new() { m => m.Monitored == monitored }
            };
        }

        private PagingSpec<Movie> Cutoff(List<QualitiesBelowCutoff> profiles = null, bool monitored = true)
        {
            return Subject.MoviesWhereCutoffUnmet(Page(monitored), profiles ?? new List<QualitiesBelowCutoff>
            {
                new(_profile.Id, new[] { Quality.DVD.Id }), new(_secondaryProfile.Id, new[] { Quality.DVD.Id })
            });
        }

        [Test]
        public void missing_secondary_is_wanted_when_primary_has_a_file_and_unmonitored_target_is_ignored()
        {
            var primaryFile = Attach(_primary, Quality.Bluray1080p);
            var wanted = Subject.MoviesWithoutFiles(Page());
            wanted.TotalRecords.Should().Be(1);
            wanted.Records.Should().ContainSingle().Which.MovieFileId.Should().Be(primaryFile.Id);
            wanted.Records.Single().MovieFile.Id.Should().Be(primaryFile.Id);
            wanted.Records.Single().QualityProfile.Id.Should().Be(_profile.Id);
            _secondary.Monitored = false;
            Mocker.Resolve<MovieEditionRepository>().SetFields(_secondary, e => e.Monitored);
            Subject.MoviesWithoutFiles(Page()).TotalRecords.Should().Be(0);
        }

        [Test]
        public void secondary_cutoff_match_does_not_populate_a_missing_primary_file()
        {
            Attach(_secondary, Quality.DVD);
            var wanted = Cutoff();
            wanted.TotalRecords.Should().Be(1);
            wanted.Records.Should().ContainSingle().Which.MovieFileId.Should().Be(0);
            wanted.Records.Single().MovieFile.Should().BeNull();
        }

        [Test]
        public void all_missing_editions_return_one_movie_with_stable_pagination_and_total()
        {
            NewMovie();
            var first = Subject.MoviesWithoutFiles(Page(page: 1, size: 1));
            var second = Subject.MoviesWithoutFiles(Page(page: 2, size: 1));
            first.TotalRecords.Should().Be(2);
            second.TotalRecords.Should().Be(2);
            first.Records.Should().ContainSingle().Which.Id.Should().Be(_movie.Id);
            second.Records.Should().ContainSingle().Which.Id.Should().NotBe(_movie.Id);
            Subject.MoviesWithoutFiles(Page(page: 3, size: 1)).Records.Should().BeEmpty();
        }

        [Test]
        public void missing_query_applies_parent_monitoring_and_year_filters_to_edition_matches()
        {
            Subject.MoviesWithoutFiles(Page(false)).TotalRecords.Should().Be(0);
            _movie.Monitored = false;
            Subject.SetFields(_movie, m => m.Monitored);
            Subject.MoviesWithoutFiles(Page()).TotalRecords.Should().Be(0);
            Subject.MoviesWithoutFiles(Page(false)).TotalRecords.Should().Be(1);
            using var connection = Db.OpenConnection();
            connection.Execute(@"UPDATE ""MovieMetadata"" SET ""Year"" = 0 WHERE ""Id"" = @Id", new { Id = _movie.MovieMetadataId });
            Subject.MoviesWithoutFiles(Page(false)).TotalRecords.Should().Be(0);
        }

        [Test]
        public void cutoff_uses_each_editions_own_profile_and_never_the_movie_default_for_a_secondary()
        {
            var primaryFile = Attach(_primary, Quality.Bluray1080p);
            Attach(_secondary, Quality.DVD);
            Cutoff(new() { new(_profile.Id, new[] { Quality.DVD.Id }) }).TotalRecords.Should().Be(0);
            var wanted = Cutoff(new() { new(_secondaryProfile.Id, new[] { Quality.DVD.Id }) });
            wanted.TotalRecords.Should().Be(1);
            wanted.Records.Should().ContainSingle().Which.MovieFileId.Should().Be(primaryFile.Id);
            wanted.Records.Single().MovieFile.Id.Should().Be(primaryFile.Id);
            wanted.Records.Single().QualityProfile.Id.Should().Be(_profile.Id);
            _secondary.Monitored = false;
            Mocker.Resolve<MovieEditionRepository>().SetFields(_secondary, e => e.Monitored);
            Cutoff().TotalRecords.Should().Be(0);
        }

        [Test]
        public void several_low_quality_editions_return_one_movie_and_empty_cutoffs_return_none()
        {
            Attach(_primary, Quality.DVD);
            Attach(_secondary, Quality.DVD);
            Cutoff().TotalRecords.Should().Be(1);
            Cutoff().Records.Should().ContainSingle();
            Cutoff(new()).TotalRecords.Should().Be(0);
            Cutoff(monitored: false).TotalRecords.Should().Be(0);
        }

        [TestCase("missing")]
        [TestCase("different-movie")]
        [TestCase("different-edition")]
        public void broken_current_file_links_are_missing_and_do_not_borrow_a_cutoff_file(string kind)
        {
            Attach(_primary, Quality.Bluray1080p);
            var file = Attach(_secondary, Quality.DVD);
            using var connection = Db.OpenConnection();
            if (kind == "missing")
            {
                connection.Execute(@"DELETE FROM ""MovieFiles"" WHERE ""Id"" = @Id", new { file.Id });
            }
            else if (kind == "different-movie")
            {
                connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieId"" = @MovieId WHERE ""Id"" = @Id", new { file.Id, MovieId = _movie.Id + 100 });
            }
            else
            {
                connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionId"" = @EditionId WHERE ""Id"" = @Id", new { file.Id, EditionId = _primary.Id });
            }

            Subject.MoviesWithoutFiles(Page()).TotalRecords.Should().Be(1);
            Cutoff().TotalRecords.Should().Be(0);
        }

        [Test]
        public void unmonitored_movie_remains_reviewable_without_enabling_effective_monitoring()
        {
            Attach(_secondary, Quality.DVD);
            _movie.Monitored = false;
            Subject.SetFields(_movie, m => m.Monitored);
            _primary.Monitored = false;
            _secondary.Monitored = false;
            Mocker.Resolve<MovieEditionRepository>().SetFields(_primary, e => e.Monitored);
            Mocker.Resolve<MovieEditionRepository>().SetFields(_secondary, e => e.Monitored);
            Subject.MoviesWithoutFiles(Page(false)).TotalRecords.Should().Be(1);
            Cutoff(monitored: false).TotalRecords.Should().Be(1);
            Subject.MoviesWithoutFiles(Page()).TotalRecords.Should().Be(0);
            Cutoff().TotalRecords.Should().Be(0);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_movie_without_any_editions_retains_missing_and_cutoff_behavior(bool hasFile)
        {
            if (hasFile)
            {
                Attach(_primary, Quality.DVD);
            }

            using var connection = Db.OpenConnection();
            connection.Execute(@"DELETE FROM ""MovieEditions"" WHERE ""MovieId"" = @Id", new { _movie.Id });
            Subject.MoviesWithoutFiles(Page()).TotalRecords.Should().Be(hasFile ? 0 : 1);
            Cutoff().TotalRecords.Should().Be(hasFile ? 1 : 0);
        }
    }
}
