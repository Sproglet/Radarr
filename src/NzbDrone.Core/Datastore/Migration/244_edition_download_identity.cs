using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(244)]
    public class edition_download_identity : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Legacy rows remain unassigned. A title or today's rules cannot prove grab intent.
            foreach (var table in new[] { "PendingReleases", "History", "DownloadHistory" })
            {
                Alter.Table(table).AddColumn("MovieEditionId").AsInt32().Nullable().Indexed();
                Alter.Table(table).AddColumn("EditionRuleRevision").AsInt32().Nullable();
            }
        }
    }
}
