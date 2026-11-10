using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.MovieStats;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieStatsTests
{
    [TestFixture]
    public class EditionStatisticsFixture : DbTest<MovieStatisticsRepository, Movie>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _secondary;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(m => m.Monitored = true).With(m => m.MovieFileId = 0).BuildNew();
            Db.Insert(_movie);
            _primary = AddEdition("Synthetic Theatrical IMAX", true);
            _secondary = AddEdition("Synthetic Directors 3D", true);
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private MovieEdition AddEdition(string name, bool monitored)
        {
            return Db.Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = name, EditionKey = name, Monitored = monitored,
                QualityProfileId = _movie.QualityProfileId
            });
        }

        private MovieFile AddFile(MovieEdition edition, long size, Quality quality, string group)
        {
            var file = Builder<MovieFile>.CreateNew()
                .With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = edition?.Id)
                .With(f => f.Size = size).With(f => f.Quality = new QualityModel(quality))
                .With(f => f.ReleaseGroup = group).With(f => f.Languages = new List<Language> { Language.English }).BuildNew();
            Db.Insert(file);
            if (edition != null)
            {
                edition.MovieFileId = file.Id;
                Db.Update(edition);
            }

            return file;
        }

        [Test]
        public void counts_all_records_once_and_only_current_owned_files_as_available()
        {
            AddFile(_primary, 101, Quality.Bluray1080p, "SyntheticFirstGroup");
            AddFile(_secondary, 202, Quality.Bluray720p, "SyntheticSecondGroup");
            AddFile(null, 303, Quality.DVD, "SyntheticUnassignedGroup");
            AddEdition("Synthetic Missing", true);
            AddEdition("Synthetic Unmonitored", false);
            // A retained superseded record consumes disk space but is not another available edition.
            var retained = AddFile(null, 404, Quality.Bluray720p, "SyntheticFirstGroup");
            retained.MovieEditionId = _secondary.Id;
            Db.Update(retained);

            var stats = Subject.MovieStatistics().Single();
            stats.MovieId.Should().Be(_movie.Id);
            stats.MovieFileCount.Should().Be(4);
            stats.SizeOnDisk.Should().Be(1010);
            stats.EditionCount.Should().Be(4);
            stats.EditionFileCount.Should().Be(2);
            stats.HasAnyEditionFile.Should().BeTrue();
            stats.MonitoredEditionCount.Should().Be(3);
            stats.MissingMonitoredEditionCount.Should().Be(1);
            stats.ReleaseGroups.Should().BeEquivalentTo(new[] { "SyntheticFirstGroup", "SyntheticSecondGroup", "SyntheticUnassignedGroup" });
            stats.MovieFileQualities.Should().BeEquivalentTo(new[] { Quality.Bluray1080p, Quality.Bluray720p, Quality.DVD });
            Subject.MovieStatistics(_movie.Id).Single().Should().BeEquivalentTo(stats);
        }

        [Test]
        public void secondary_file_does_not_fill_the_missing_primary()
        {
            AddFile(_secondary, 202, Quality.Bluray720p, "SyntheticSecondGroup");
            var stats = Subject.MovieStatistics(_movie.Id).Single();
            stats.EditionFileCount.Should().Be(1);
            stats.HasAnyEditionFile.Should().BeTrue();
            stats.MissingMonitoredEditionCount.Should().Be(1);
            Db.All<Movie>().Single().MovieFileId.Should().Be(0);
        }

        [TestCase("missing")]
        [TestCase("different-movie")]
        [TestCase("different-edition")]
        [TestCase("unassigned")]
        public void invalid_current_links_are_missing_and_never_count_as_available(string kind)
        {
            var file = AddFile(_secondary, 202, Quality.Bluray720p, "SyntheticSecondGroup");
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            switch (kind)
            {
                case "missing":
                    connection.Execute(@"DELETE FROM ""MovieFiles"" WHERE ""Id"" = @Id", new { file.Id });
                    break;
                case "different-movie":
                    connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieId"" = @MovieId WHERE ""Id"" = @Id", new { file.Id, MovieId = _movie.Id + 100 });
                    break;
                case "different-edition":
                    connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionId"" = @EditionId WHERE ""Id"" = @Id", new { file.Id, EditionId = _primary.Id });
                    break;
                case "unassigned":
                    connection.Execute(@"UPDATE ""MovieFiles"" SET ""MovieEditionId"" = NULL WHERE ""Id"" = @Id", new { file.Id });
                    break;
            }

            var stats = Subject.MovieStatistics(_movie.Id).Single();
            stats.EditionFileCount.Should().Be(0);
            stats.HasAnyEditionFile.Should().BeFalse();
            stats.MissingMonitoredEditionCount.Should().Be(2);
        }

        [Test]
        public void master_monitor_switch_disables_wanted_counts_without_hiding_files_or_preferences()
        {
            AddFile(_secondary, 202, Quality.Bluray720p, "SyntheticSecondGroup");
            _movie.Monitored = false;
            Db.Update(_movie);
            var stats = Subject.MovieStatistics(_movie.Id).Single();
            stats.MonitoredEditionCount.Should().Be(0);
            stats.MissingMonitoredEditionCount.Should().Be(0);
            stats.EditionCount.Should().Be(2);
            stats.EditionFileCount.Should().Be(1);
            Db.All<MovieEdition>().Should().OnlyContain(e => e.Monitored);
        }

        [Test]
        public void all_movies_are_returned_once_including_empty_and_legacy_movies_without_editions()
        {
            AddFile(_secondary, 202, Quality.Bluray720p, "SyntheticSecondGroup");
            var legacy = Builder<Movie>.CreateNew().With(m => m.MovieFileId = 0).With(m => m.MovieMetadataId = _movie.MovieMetadataId + 100).BuildNew();
            Db.Insert(legacy);
            var all = Subject.MovieStatistics();
            all.Should().HaveCount(2);
            var stats = all.Single(s => s.MovieId == legacy.Id);
            stats.MovieFileCount.Should().Be(0);
            stats.SizeOnDisk.Should().Be(0);
            stats.EditionCount.Should().Be(0);
            stats.EditionFileCount.Should().Be(0);
            stats.MonitoredEditionCount.Should().Be(0);
            stats.MissingMonitoredEditionCount.Should().Be(0);
            Subject.MovieStatistics(legacy.Id).Single().Should().BeEquivalentTo(stats);
            Subject.MovieStatistics(legacy.Id + 100).Should().BeEmpty();
        }
    }
}
