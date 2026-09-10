using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(243)]
    public class add_movie_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("MovieEditions")
                .WithColumn("MovieId").AsInt32().Indexed()
                .WithColumn("EditionKey").AsString()
                .WithColumn("Name").AsString()
                .WithColumn("Monitored").AsBoolean()
                .WithColumn("QualityProfileId").AsInt32()
                .WithColumn("MovieFileId").AsInt32().Nullable().Indexed()
                .WithColumn("LastSearchTime").AsDateTime().Nullable()
                .WithColumn("MatchRules").AsString().WithDefaultValue("[]")
                .WithColumn("Attributes").AsString().WithDefaultValue("{}")
                .WithColumn("RuleRevision").AsInt32().WithDefaultValue(1);

            Create.Index().OnTable("MovieEditions")
                .OnColumn("MovieId").Ascending()
                .OnColumn("EditionKey").Ascending()
                .WithOptions().Unique();

            Alter.Table("Movies").AddColumn("PrimaryEditionId").AsInt32().Nullable().Indexed();
            Alter.Table("MovieFiles").AddColumn("MovieEditionId").AsInt32().Nullable().Indexed();

            // Preserve recorded labels without depending on a parser that can change later.
            // Extra files remain unassigned; a single legacy pointer cannot establish their identity.
            Execute.Sql(@"INSERT INTO ""MovieEditions""
                          (""MovieId"", ""EditionKey"", ""Name"", ""Monitored"", ""QualityProfileId"", ""MovieFileId"", ""LastSearchTime"")
                          SELECT m.""Id"", 'legacy-primary',
                                 COALESCE(NULLIF(TRIM(f.""Edition""), ''), 'Theatrical'),
                                 m.""Monitored"", m.""QualityProfileId"", f.""Id"", m.""LastSearchTime""
                          FROM ""Movies"" m
                          LEFT JOIN ""MovieFiles"" f ON f.""Id"" = m.""MovieFileId"" AND f.""MovieId"" = m.""Id""");

            Execute.Sql(@"UPDATE ""Movies""
                          SET ""PrimaryEditionId"" = (SELECT e.""Id"" FROM ""MovieEditions"" e
                                                      WHERE e.""MovieId"" = ""Movies"".""Id"" AND e.""EditionKey"" = 'legacy-primary')");

            Execute.Sql(@"UPDATE ""MovieFiles""
                          SET ""MovieEditionId"" = (SELECT e.""Id"" FROM ""MovieEditions"" e
                                                   WHERE e.""MovieFileId"" = ""MovieFiles"".""Id"" AND e.""MovieId"" = ""MovieFiles"".""MovieId"")");

            // Repair dangling and cross-movie compatibility pointers without deleting any file rows.
            Execute.Sql(@"UPDATE ""Movies""
                          SET ""MovieFileId"" = COALESCE((SELECT e.""MovieFileId"" FROM ""MovieEditions"" e
                                                         WHERE e.""Id"" = ""Movies"".""PrimaryEditionId""), 0)");
        }
    }
}
