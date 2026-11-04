using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class EditionMigrationCompatibilityFixture : MigrationTest<edition_download_identity>
    {
        internal static void SeedLibrary(NzbDroneMigrationBase m)
        {
            m.Execute.Sql(@"INSERT INTO ""Movies"" (""MovieMetadataId"",""Monitored"",""MinimumAvailability"",""QualityProfileId"",""MovieFileId"",""Path"")
                VALUES (1,true,4,7,0,'/synthetic/feature')");
            m.Execute.Sql(@"INSERT INTO ""MovieFiles"" (""MovieId"",""RelativePath"",""Quality"",""IndexerFlags"",""Size"",""DateAdded"",""Languages"",""Edition"",""MediaInfo"")
                VALUES (1,'Synthetic.Feature.2026.mkv','{}',0,1234,'2026-01-01','[1]','Theatrical IMAX',
                '{""audioLanguages"": [""en""], ""audioFormat"": ""AAC"", ""audioChannels"": 2, ""subtitles"": [""fr""]}')");
            m.Execute.Sql(@"UPDATE ""Movies"" SET ""MovieFileId"" = 1");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void old_feature_database_preserves_links_and_converts_media_info(bool identityAlreadyApplied)
        {
            using var db = WithDapperMigrationTestDb(m =>
            {
                SeedLibrary(m);
                add_movie_editions.CreateStorage(m);
            });
            db.Execute(@"UPDATE ""MovieEditions"" SET ""Name"" = 'Synthetic Preserved', ""Attributes"" = '{""cut"":""theatrical"",""framing"":""imax""}', ""RuleRevision"" = 9");
            db.Execute(@"UPDATE ""VersionInfo"" SET ""Description"" = 'add_movie_editions' WHERE ""Version"" = 243");
            if (!identityAlreadyApplied)
            {
                foreach (var table in new[] { "PendingReleases", "History", "DownloadHistory" })
                {
                    var indexQuery = db is Npgsql.NpgsqlConnection
                        ? "SELECT indexname FROM pg_indexes WHERE tablename = @Table AND indexdef LIKE '%MovieEditionId%'"
                        : "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @Table AND sql LIKE '%MovieEditionId%'";
                    foreach (var index in db.Query<string>(indexQuery, new { Table = table }))
                    {
                        db.Execute("DROP INDEX \"" + index.Replace("\"", "\"\"") + "\"");
                    }

                    db.Execute("ALTER TABLE \"" + table + "\" DROP COLUMN \"MovieEditionId\"");
                    db.Execute("ALTER TABLE \"" + table + "\" DROP COLUMN \"EditionRuleRevision\"");
                }

                db.Execute(@"DELETE FROM ""VersionInfo"" WHERE ""Version"" = 244");
            }

            Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType.Main));

            db.QuerySingle<int>(@"SELECT COUNT(*) FROM ""MovieEditions""").Should().Be(1);
            db.QuerySingle<int>(@"SELECT ""MovieFileId"" FROM ""MovieEditions""").Should().Be(1);
            db.QuerySingle<int>(@"SELECT ""PrimaryEditionId"" FROM ""Movies""").Should().Be(1);
            db.QuerySingle<string>(@"SELECT ""Name"" FROM ""MovieEditions""").Should().Be("Synthetic Preserved");
            db.QuerySingle<int>(@"SELECT ""RuleRevision"" FROM ""MovieEditions""").Should().Be(9);
            db.QuerySingle<string>(@"SELECT ""Description"" FROM ""VersionInfo"" WHERE ""Version"" = 243").Should().Be("add_movie_editions");
            var info = JsonNode.Parse(db.QuerySingle<string>(@"SELECT ""MediaInfo"" FROM ""MovieFiles"""));
            info["audioStreams"][0]["language"].GetValue<string>().Should().Be("en");
            info["subtitleStreams"][0]["language"].GetValue<string>().Should().Be("fr");
            db.QuerySingle<int>(@"SELECT COUNT(*) FROM ""VersionInfo"" WHERE ""Version"" IN (244,245)").Should().Be(2);
        }
    }

    public class UpstreamEditionMigrationCompatibilityFixture : MigrationTest<mediainfo_multiple_streams>
    {
        [Test]
        public void upstream_database_gains_editions_without_rewriting_media_info_or_version_history()
        {
            using var db = WithDapperMigrationTestDb(EditionMigrationCompatibilityFixture.SeedLibrary);
            var info = db.QuerySingle<string>(@"SELECT ""MediaInfo"" FROM ""MovieFiles""");
            var description = db.QuerySingle<string>(@"SELECT ""Description"" FROM ""VersionInfo"" WHERE ""Version"" = 243");
            JsonNode.Parse(info)["audioStreams"][0]["language"].GetValue<string>().Should().Be("en");

            Mocker.Resolve<DbFactory>().Create(new MigrationContext(MigrationType.Main));

            db.QuerySingle<int>(@"SELECT COUNT(*) FROM ""MovieEditions""").Should().Be(1);
            db.QuerySingle<int>(@"SELECT ""MovieFileId"" FROM ""MovieEditions""").Should().Be(1);
            db.QuerySingle<int>(@"SELECT ""MovieEditionId"" FROM ""MovieFiles""").Should().Be(1);
            db.QuerySingle<string>(@"SELECT ""MediaInfo"" FROM ""MovieFiles""").Should().Be(info);
            db.QuerySingle<string>(@"SELECT ""Description"" FROM ""VersionInfo"" WHERE ""Version"" = 243").Should().Be(description);
        }
    }

}
