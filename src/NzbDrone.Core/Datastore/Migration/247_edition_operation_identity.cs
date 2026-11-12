using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(247)]
    public class edition_operation_identity : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Existing rows have no provable shared operation ID. Never infer one from a client ID or title.
            foreach (var table in new[] { "History", "DownloadHistory" })
            {
                Alter.Table(table).AddColumn("EditionOperationId").AsString(36).Nullable();
                Execute.Sql($@"CREATE UNIQUE INDEX ""IX_{table}_EditionOperationGrab"" ON ""{table}"" (""EditionOperationId"")
                    WHERE ""EventType"" = 1 AND ""EditionOperationId"" IS NOT NULL");
            }
        }
    }
}
