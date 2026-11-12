using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dapper;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class movie_edition_search_time_with_timezoneFixture : MigrationTest<movie_edition_search_time_with_timezone>
    {
        [Test]
        public void preserves_utc_instants_nulls_and_definitions_in_non_utc_migration_sessions(
            [Values("America/New_York", "Pacific/Auckland")] string timezone,
            [Values(false, true)] bool alreadyWithTimezone)
        {
            var expected = new DateTime?[]
            {
                new DateTime(2026, 3, 8, 6, 59, 59, DateTimeKind.Utc).AddTicks(1234560),
                new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc).AddTicks(6543210),
                new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc),
                null
            };
            List<SearchEdition> before = null;
            string sqliteSchema = null;
            using var db = WithDapperMigrationTestDb(m => m.Execute.WithConnection((connection, transaction) =>
            {
                var postgres = connection is NpgsqlConnection;
                if (postgres)
                {
                    connection.Execute("SET LOCAL TIME ZONE '" + timezone + "'", transaction: transaction);
                    if (alreadyWithTimezone)
                    {
                        connection.Execute(@"ALTER TABLE ""MovieEditions"" ALTER COLUMN ""LastSearchTime""
                            TYPE timestamp with time zone USING ""LastSearchTime"" AT TIME ZONE 'UTC'", transaction: transaction);
                    }
                }
                else
                {
                    sqliteSchema = connection.QuerySingle<string>("SELECT sql FROM sqlite_master WHERE name='MovieEditions'", transaction: transaction);
                }

                for (var index = 0; index < expected.Length; index++)
                {
                    var timestamp = postgres
                        ? alreadyWithTimezone ? "CAST(@Searched AS timestamp without time zone) AT TIME ZONE 'UTC'" : "CAST(@Searched AS timestamp without time zone)"
                        : "@Searched";
                    connection.Execute(@"INSERT INTO ""MovieEditions""
                        (""MovieId"",""EditionKey"",""Name"",""Monitored"",""QualityProfileId"",""LastSearchTime"",""Attributes"",""MatchRules"",""RuleRevision"")
                        VALUES (17,@Key,@Name,TRUE,7," + timestamp + ",@Attributes,'[\"SyntheticGroup$\"]',9)",
                        new
                        {
                            Key = "synthetic-search-" + index,
                            Name = "Synthetic Edition " + index,
                            Searched = expected[index]?.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                            Attributes = index % 2 == 0 ? "{\"cut\":\"theatrical\",\"framing\":\"imax\"}" : "{\"cut\":\"directors-cut\",\"stereoscopic\":\"3d\"}"
                        },
                        transaction);
                }

                before = connection.Query<SearchEdition>("SELECT * FROM \"MovieEditions\" ORDER BY \"Id\"", transaction: transaction).ToList();
            }));

            var after = db.Query<SearchEdition>("SELECT * FROM \"MovieEditions\" ORDER BY \"Id\"").ToList();
            after.Should().BeEquivalentTo(before, options => options.Excluding(e => e.LastSearchTime).WithStrictOrdering());
            after.Select(e => e.LastSearchTime).Should().Equal(expected);
            after.Where(e => e.LastSearchTime.HasValue).Should().OnlyContain(e => e.LastSearchTime.Value.Kind == DateTimeKind.Utc);
            if (db is NpgsqlConnection)
            {
                db.QuerySingle<string>(@"SELECT data_type FROM information_schema.columns
                    WHERE table_schema = current_schema() AND table_name = 'MovieEditions' AND column_name = 'LastSearchTime'")
                    .Should().Be("timestamp with time zone");
                db.Execute("SET TIME ZONE '" + timezone + "'");
                db.Query<DateTime?>("SELECT \"LastSearchTime\" FROM \"MovieEditions\" ORDER BY \"Id\"").Should().Equal(expected);
            }
            else
            {
                db.QuerySingle<string>("SELECT sql FROM sqlite_master WHERE name='MovieEditions'").Should().Be(sqliteSchema);
            }

            Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType.Main));
            db.Query<SearchEdition>("SELECT * FROM \"MovieEditions\" ORDER BY \"Id\"")
                .Should().BeEquivalentTo(after, options => options.WithStrictOrdering());
        }

        private class SearchEdition
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public string EditionKey { get; set; }
            public string Name { get; set; }
            public bool Monitored { get; set; }
            public int QualityProfileId { get; set; }
            public int? MovieFileId { get; set; }
            public DateTime? LastSearchTime { get; set; }
            public string Attributes { get; set; }
            public string MatchRules { get; set; }
            public int RuleRevision { get; set; }
        }
    }

    public class EditionUtcFreshMigrationFixture : MigrationTest<mediainfo_multiple_streams>
    {
        [TestCase("America/New_York")]
        [TestCase("Pacific/Auckland")]
        public void fresh_edition_backfill_preserves_parent_utc_time_and_runtime_timezone(string timezone)
        {
            var searched = new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc).AddTicks(1234560);
            using var db = WithDapperMigrationTestDb(m =>
            {
                EditionMigrationCompatibilityFixture.SeedLibrary(m);
                m.Execute.WithConnection((connection, transaction) => connection.Execute(
                    "UPDATE \"Movies\" SET \"LastSearchTime\" = @Searched", new { Searched = searched }, transaction));
            });
            var factory = Mocker.Resolve<IConnectionStringFactory>();
            if (db is NpgsqlConnection)
            {
                factory.MainDbConnection.ConnectionString = new NpgsqlConnectionStringBuilder(factory.MainDbConnection.ConnectionString)
                {
                    Timezone = timezone
                }.ConnectionString;
            }

            var runtimeConnectionString = factory.MainDbConnection.ConnectionString;
            var upgraded = Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType.Main));
            using var runtime = upgraded.OpenConnection();

            runtime.QuerySingle<DateTime>("SELECT \"LastSearchTime\" FROM \"MovieEditions\"").Should().Be(searched);
            runtime.QuerySingle<DateTime>("SELECT \"LastSearchTime\" FROM \"Movies\"").Should().Be(searched);
            runtime.QuerySingle<int>("SELECT \"MovieFileId\" FROM \"MovieEditions\"").Should().Be(1);
            runtime.QuerySingle<int>("SELECT \"PrimaryEditionId\" FROM \"Movies\"").Should().Be(1);
            factory.MainDbConnection.ConnectionString.Should().Be(runtimeConnectionString);
            if (db is NpgsqlConnection)
            {
                runtime.QuerySingle<string>("SHOW TIME ZONE").Should().Be(timezone);
            }
        }
    }

    public class EditionUtcLegacyFeatureMigrationFixture : MigrationTest<edition_download_identity>
    {
        [TestCase("America/New_York")]
        [TestCase("Pacific/Auckland")]
        public void old_feature_schema_retains_search_time_links_and_recorded_definitions(string timezone)
        {
            var searched = new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc).AddTicks(6543210);
            using var db = WithDapperMigrationTestDb(m =>
            {
                EditionMigrationCompatibilityFixture.SeedLibrary(m);
                add_movie_editions.CreateStorage(m);
            });
            db.Execute(@"UPDATE ""VersionInfo"" SET ""Description"" = 'add_movie_editions' WHERE ""Version"" = 243");
            db.Execute(@"UPDATE ""MovieEditions"" SET ""Name"" = 'Synthetic Directors 3D IMAX',
                ""RuleRevision"" = 9, ""Attributes"" = '{""cut"":""directors-cut"",""stereoscopic"":""3d"",""framing"":""imax""}',
                ""LastSearchTime"" = '2026-03-08 07:00:00.654321'");
            var before = db.QuerySingle<string>("SELECT \"Attributes\" FROM \"MovieEditions\"");
            var factory = Mocker.Resolve<IConnectionStringFactory>();
            if (db is NpgsqlConnection)
            {
                factory.MainDbConnection.ConnectionString = new NpgsqlConnectionStringBuilder(factory.MainDbConnection.ConnectionString)
                {
                    Timezone = timezone
                }.ConnectionString;
            }

            Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType.Main));

            db.QuerySingle<DateTime>("SELECT \"LastSearchTime\" FROM \"MovieEditions\"").Should().Be(searched);
            db.QuerySingle<string>("SELECT \"Attributes\" FROM \"MovieEditions\"").Should().Be(before);
            db.QuerySingle<string>("SELECT \"Name\" FROM \"MovieEditions\"").Should().Be("Synthetic Directors 3D IMAX");
            db.QuerySingle<int>("SELECT \"RuleRevision\" FROM \"MovieEditions\"").Should().Be(9);
            db.QuerySingle<int>("SELECT \"MovieFileId\" FROM \"MovieEditions\"").Should().Be(1);
            db.QuerySingle<int>("SELECT \"MovieEditionId\" FROM \"MovieFiles\"").Should().Be(1);
            db.QuerySingle<int>("SELECT COUNT(*) FROM \"MovieEditions\"").Should().Be(1);
            db.QuerySingle<string>("SELECT \"Description\" FROM \"VersionInfo\" WHERE \"Version\"=243").Should().Be("add_movie_editions");
        }
    }
}
