using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(248)]
    public class edition_submission_journal : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("MovieEditionSubmissions")
                .WithColumn("OperationId").AsString(36).Unique()
                .WithColumn("MovieId").AsInt32().Indexed()
                .WithColumn("MovieEditionId").AsInt32().Indexed()
                .WithColumn("EditionRuleRevision").AsInt32()
                .WithColumn("EditionName").AsString()
                .WithColumn("EditionAttributes").AsString()
                .WithColumn("QualityProfileId").AsInt32()
                .WithColumn("MovieFileId").AsInt32().Nullable()
                .WithColumn("MoviePath").AsString()
                .WithColumn("DownloadClientId").AsInt32()
                .WithColumn("DownloadClientName").AsString()
                .WithColumn("Release").AsString()
                .WithColumn("ParsedMovieInfo").AsString()
                .WithColumn("DownloadId").AsString().Nullable()
                .WithColumn("State").AsInt32()
                .WithColumn("CreatedAt").AsDateTime()
                .WithColumn("UpdatedAt").AsDateTime();

            Execute.Sql(@"CREATE UNIQUE INDEX ""IX_MovieEditionSubmissions_ActiveTarget""
                ON ""MovieEditionSubmissions"" (""MovieEditionId"") WHERE ""State"" <> 3");
            IfDatabase("PostgreSQL").Execute.Sql(@"ALTER TABLE ""MovieEditionSubmissions""
                ALTER COLUMN ""CreatedAt"" TYPE timestamp with time zone USING ""CreatedAt"" AT TIME ZONE 'UTC',
                ALTER COLUMN ""UpdatedAt"" TYPE timestamp with time zone USING ""UpdatedAt"" AT TIME ZONE 'UTC'");
        }
    }
}
