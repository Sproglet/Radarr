using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(249)]
    public class edition_grab_payload_snapshots : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Older intent cannot reconstruct volatile grab metadata. Keep missing snapshots explicit.
            Alter.Table("MovieEditionSubmissions")
                .AddColumn("HistoryData").AsString().Nullable()
                .AddColumn("DownloadHistoryData").AsString().Nullable()
                .AddColumn("Languages").AsString().Nullable()
                .AddColumn("CustomFormatScore").AsInt32().WithDefaultValue(0)
                .AddColumn("MovieMatchType").AsInt32().WithDefaultValue(0)
                .AddColumn("ReleaseSource").AsInt32().WithDefaultValue(0)
                .AddColumn("DownloadClient").AsString().Nullable();
        }
    }
}
