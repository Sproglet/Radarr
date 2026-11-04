using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class edition_download_identityFixture : MigrationTest<edition_download_identity>
    {
        [Test]
        public void should_leave_all_legacy_download_identity_unassigned()
        {
            using var db = WithDapperMigrationTestDb(c =>
            {
                c.Execute.Sql(@"INSERT INTO ""PendingReleases"" (""MovieId"",""Title"",""Added"",""ParsedMovieInfo"",""Release"",""Reason"",""AdditionalInfo"")
                    VALUES (42,'Synthetic.Voyage.2026.IMAX-SyntheticGroup','2026-01-02 00:00:00','{}','{}',0,'{}')");
                c.Execute.Sql(@"INSERT INTO ""History"" (""MovieId"",""SourceTitle"",""Quality"",""Date"",""EventType"",""Data"",""Languages"")
                    VALUES (42,'Synthetic.Voyage.2026.Directors.Cut-SyntheticGroup','{}','2026-01-02 00:00:00',1,'{}','[]')");
                c.Execute.Sql(@"INSERT INTO ""DownloadHistory"" (""MovieId"",""DownloadId"",""SourceTitle"",""Date"",""EventType"",""Protocol"",""IndexerId"",""DownloadClientId"",""Release"",""Data"")
                    VALUES (42,'synthetic-download','Synthetic.Voyage.2026-SyntheticGroup','2026-01-02 00:00:00',1,1,0,0,'{}','{}')");
            });
            foreach (var table in new[] { "PendingReleases", "History", "DownloadHistory" })
            {
                db.QuerySingle<int>("SELECT COUNT(*) FROM \"" + table + "\" WHERE \"MovieEditionId\" IS NULL AND \"EditionRuleRevision\" IS NULL").Should().Be(1);
            }
        }
    }
}
