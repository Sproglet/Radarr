using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(250)]
    public class edition_reconciliation_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.Sql(@"CREATE INDEX ""IX_MovieEditionSubmissions_Reconciliation"" ON ""MovieEditionSubmissions"" (""Id"") WHERE ""State"" IN (1,2)");
            Execute.Sql(@"CREATE INDEX ""IX_DownloadHistory_EditionOperationTerminal"" ON ""DownloadHistory"" (""EditionOperationId"")
                WHERE ""EventType"" IN (2,3,4) AND ""EditionOperationId"" IS NOT NULL");
        }
    }
}
