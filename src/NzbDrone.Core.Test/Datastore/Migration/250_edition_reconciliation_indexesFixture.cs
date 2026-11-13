using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class edition_reconciliation_indexesFixture : MigrationTest<edition_reconciliation_indexes>
    {
        [Test]
        public void indexing_keeps_old_unknown_intent_and_payloads_unchanged()
        {
            using var db = WithDapperMigrationTestDb(c => c.Execute.Sql(@"INSERT INTO ""MovieEditionSubmissions""
                (""OperationId"",""MovieId"",""MovieEditionId"",""EditionRuleRevision"",""EditionName"",""EditionAttributes"",""QualityProfileId"",
                ""MoviePath"",""DownloadClientId"",""DownloadClientName"",""Release"",""ParsedMovieInfo"",""State"",""CreatedAt"",""UpdatedAt"")
                VALUES ('00000000-0000-4000-8000-000000000041',42,77,3,'Synthetic Directors 3D','{}',1,'/synthetic',41,'Synthetic Client','{}','{}',0,'2026-11-13T00:00:00Z','2026-11-13T00:00:00Z')"));

            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionSubmissions"" WHERE ""State"" = 0 AND ""DownloadId"" IS NULL
                AND ""HistoryData"" IS NULL AND ""DownloadHistoryData"" IS NULL AND ""Languages"" IS NULL AND ""EditionRuleRevision"" = 3").Should().Be(1);
            if (db.GetType().Name.Contains("Npgsql"))
            {
                db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname IN
                    ('IX_MovieEditionSubmissions_Reconciliation','IX_DownloadHistory_EditionOperationTerminal')").Should().Be(2);
            }
            else
            {
                db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN
                    ('IX_MovieEditionSubmissions_Reconciliation','IX_DownloadHistory_EditionOperationTerminal')").Should().Be(2);
            }
        }
    }
}
