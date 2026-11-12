using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class edition_operation_identityFixture : MigrationTest<edition_operation_identity>
    {
        [Test]
        public void legacy_history_keeps_unknown_operation_identity_and_existing_evidence()
        {
            using var db = WithDapperMigrationTestDb(c =>
            {
                c.Execute.Sql(@"INSERT INTO ""History"" (""MovieId"",""MovieEditionId"",""EditionRuleRevision"",""SourceTitle"",""Quality"",""Date"",""EventType"",""Data"",""Languages"")
                    VALUES (42,77,3,'Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup','{}','2026-01-02 00:00:00',1,'{""movieEditionName"":""Synthetic Directors 3D""}','[]')");
                c.Execute.Sql(@"INSERT INTO ""DownloadHistory"" (""MovieId"",""MovieEditionId"",""EditionRuleRevision"",""DownloadId"",""SourceTitle"",""Date"",""EventType"",""Protocol"",""IndexerId"",""DownloadClientId"",""Release"",""Data"")
                    VALUES (42,77,3,'synthetic-download','Synthetic.Voyage.2026.Directors.Cut.3D-SyntheticGroup','2026-01-02 00:00:00',1,1,0,41,'{}','{""movieEditionName"":""Synthetic Directors 3D""}')");
            });
            foreach (var table in new[] { "History", "DownloadHistory" })
            {
                db.QuerySingle<int>($@"SELECT COUNT(*) FROM ""{table}"" WHERE ""EditionOperationId"" IS NULL
                    AND ""MovieEditionId"" = 77 AND ""EditionRuleRevision"" = 3").Should().Be(1);
                db.QuerySingle<string>($@"SELECT ""Data"" FROM ""{table}""").Should().Be("{\"movieEditionName\":\"Synthetic Directors 3D\"}");
            }
        }
    }
}
