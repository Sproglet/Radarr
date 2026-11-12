using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class edition_grab_payload_snapshotsFixture : MigrationTest<edition_grab_payload_snapshots>
    {
        [Test]
        public void old_intent_does_not_gain_invented_replay_payloads()
        {
            using var db = WithDapperMigrationTestDb(c => c.Execute.Sql(@"INSERT INTO ""MovieEditionSubmissions""
                (""OperationId"",""MovieId"",""MovieEditionId"",""EditionRuleRevision"",""EditionName"",""EditionAttributes"",""QualityProfileId"",
                ""MoviePath"",""DownloadClientId"",""DownloadClientName"",""Release"",""ParsedMovieInfo"",""State"",""CreatedAt"",""UpdatedAt"")
                VALUES ('00000000-0000-4000-8000-000000000041',42,77,3,'Synthetic Directors 3D','{}',1,'/synthetic',41,'Synthetic Client','{}','{}',0,'2026-11-12T12:00:00Z','2026-11-12T12:00:00Z')"));

            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionSubmissions"" WHERE ""HistoryData"" IS NULL
                AND ""DownloadHistoryData"" IS NULL AND ""Languages"" IS NULL AND ""EditionRuleRevision"" = 3 AND ""State"" = 0").Should().Be(1);
        }
    }
}
