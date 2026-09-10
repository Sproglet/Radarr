using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class add_movie_editionsFixture : MigrationTest<add_movie_editions>
    {
        [TestCase(null, "Theatrical")]
        [TestCase("", "Theatrical")]
        [TestCase("   ", "Theatrical")]
        [TestCase("Director's Cut", "Director's Cut")]
        [TestCase("IMAX 3D", "IMAX 3D")]
        [TestCase("Custom Edition", "Custom Edition")]
        public void should_preserve_current_file_and_recorded_label(string label, string expectedName)
        {
            var searched = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            using var db = WithDapperMigrationTestDb(c =>
            {
                InsertMovie(c, 1, 11, false, searched);
                InsertFile(c, 11, 1, label);
            });

            var edition = db.Query<Edition243>("SELECT * FROM \"MovieEditions\"").Single();
            edition.Name.Should().Be(expectedName);
            edition.MovieId.Should().Be(1);
            edition.MovieFileId.Should().Be(11);
            edition.Monitored.Should().BeFalse();
            edition.QualityProfileId.Should().Be(7);
            edition.LastSearchTime.Should().Be(searched);
            edition.MatchRules.Should().Be("[]");
            edition.Attributes.Should().Be("{}");
            edition.RuleRevision.Should().Be(1);
            db.QuerySingle<int>("SELECT \"PrimaryEditionId\" FROM \"Movies\"").Should().Be(edition.Id);
            db.QuerySingle<int>("SELECT \"MovieEditionId\" FROM \"MovieFiles\"").Should().Be(edition.Id);
            db.QuerySingle<string>("SELECT \"RelativePath\" FROM \"MovieFiles\"").Should().Be("Example.Feature.2026.mkv");
        }

        [TestCase(0)]
        [TestCase(999)]
        public void should_create_missing_theatrical_target_for_empty_or_broken_pointer(int fileId)
        {
            using var db = WithDapperMigrationTestDb(c => InsertMovie(c, 1, fileId));

            var edition = db.Query<Edition243>("SELECT * FROM \"MovieEditions\"").Single();
            edition.Name.Should().Be("Theatrical");
            edition.MovieFileId.Should().BeNull();
            edition.Monitored.Should().BeTrue();
            db.QuerySingle<int>("SELECT \"MovieFileId\" FROM \"Movies\"").Should().Be(0);
        }

        [Test]
        public void should_preserve_additional_files_without_guessing_their_edition()
        {
            using var db = WithDapperMigrationTestDb(c =>
            {
                InsertMovie(c, 1, 11);
                InsertFile(c, 11, 1, "Extended");
                InsertFile(c, 12, 1, "Extended");
                InsertFile(c, 13, 1, "IMAX 3D");
            });

            db.Query<Edition243>("SELECT * FROM \"MovieEditions\"").Should().ContainSingle();
            db.QuerySingle<int>("SELECT COUNT(*) FROM \"MovieFiles\"").Should().Be(3);
            db.QuerySingle<int>("SELECT COUNT(*) FROM \"MovieFiles\" WHERE \"MovieEditionId\" IS NULL").Should().Be(2);
        }

        [Test]
        public void should_not_assign_another_movies_file()
        {
            using var db = WithDapperMigrationTestDb(c =>
            {
                InsertMovie(c, 1, 11);
                InsertMovie(c, 2, 11);
                InsertFile(c, 11, 2, "Extended");
            });

            var editions = db.Query<Edition243>("SELECT * FROM \"MovieEditions\" ORDER BY \"MovieId\"").ToList();
            editions[0].MovieFileId.Should().BeNull();
            editions[1].MovieFileId.Should().Be(11);
            db.QuerySingle<int>("SELECT \"MovieEditionId\" FROM \"MovieFiles\"").Should().Be(editions[1].Id);
            db.QuerySingle<int>("SELECT \"MovieFileId\" FROM \"Movies\" WHERE \"Id\" = 1").Should().Be(0);
        }

        private static void InsertMovie(add_movie_editions migration, int id, int fileId, bool monitored = true, DateTime? searched = null)
        {
            migration.Insert.IntoTable("Movies").Row(new
            {
                Id = id,
                MovieMetadataId = id,
                Monitored = monitored,
                MinimumAvailability = 4,
                QualityProfileId = 7,
                MovieFileId = fileId,
                Path = "/movies/Example Feature " + id,
                LastSearchTime = searched
            });
        }

        private static void InsertFile(add_movie_editions migration, int id, int movieId, string edition)
        {
            migration.Insert.IntoTable("MovieFiles").Row(new
            {
                Id = id,
                MovieId = movieId,
                RelativePath = "Example.Feature.2026.mkv",
                Quality = "{}",
                IndexerFlags = 0,
                Size = 1234,
                DateAdded = new DateTime(2026, 1, 1),
                Languages = "[1]",
                Edition = edition
            });
        }

        private class Edition243
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public string Name { get; set; }
            public bool Monitored { get; set; }
            public int QualityProfileId { get; set; }
            public int? MovieFileId { get; set; }
            public DateTime? LastSearchTime { get; set; }
            public string MatchRules { get; set; }
            public string Attributes { get; set; }
            public int RuleRevision { get; set; }
        }
    }
}
