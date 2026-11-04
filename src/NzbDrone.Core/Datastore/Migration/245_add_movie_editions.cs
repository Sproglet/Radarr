using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(245)]
    public class add_movie_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("MovieEditions").Exists())
            {
                // Older feature builds recorded edition storage as migration 243. That
                // version now belongs to upstream media info, so replay its idempotent
                // conversion here without rewriting the existing version history or links.
                new mediainfo_multiple_streams().ApplyTo(this);
                return;
            }

            CreateStorage(this);
        }

        internal static void CreateStorage(NzbDroneMigrationBase migration)
        {
            migration.Create.TableForModel("MovieEditions")
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

            migration.Create.Index().OnTable("MovieEditions")
                .OnColumn("MovieId").Ascending()
                .OnColumn("EditionKey").Ascending()
                .WithOptions().Unique();

            migration.Alter.Table("Movies").AddColumn("PrimaryEditionId").AsInt32().Nullable().Indexed();
            migration.Alter.Table("MovieFiles").AddColumn("MovieEditionId").AsInt32().Nullable().Indexed();

            // Preserve recorded labels without depending on a parser that can change later.
            // Extra files remain unassigned; a single legacy pointer cannot establish their identity.
            migration.Execute.Sql(@"INSERT INTO ""MovieEditions""
                          (""MovieId"", ""EditionKey"", ""Name"", ""Monitored"", ""QualityProfileId"", ""MovieFileId"", ""LastSearchTime"")
                          SELECT m.""Id"", 'legacy-primary',
                                 COALESCE(NULLIF(TRIM(f.""Edition""), ''), 'Theatrical'),
                                 m.""Monitored"", m.""QualityProfileId"", f.""Id"", m.""LastSearchTime""
                          FROM ""Movies"" m
                          LEFT JOIN ""MovieFiles"" f ON f.""Id"" = m.""MovieFileId"" AND f.""MovieId"" = m.""Id""");

            migration.Execute.Sql(@"UPDATE ""Movies""
                          SET ""PrimaryEditionId"" = (SELECT e.""Id"" FROM ""MovieEditions"" e
                                                      WHERE e.""MovieId"" = ""Movies"".""Id"" AND e.""EditionKey"" = 'legacy-primary')");

            migration.Execute.Sql(@"UPDATE ""MovieFiles""
                          SET ""MovieEditionId"" = (SELECT e.""Id"" FROM ""MovieEditions"" e
                                                   WHERE e.""MovieFileId"" = ""MovieFiles"".""Id"" AND e.""MovieId"" = ""MovieFiles"".""MovieId"")");

            // Repair dangling and cross-movie compatibility pointers without deleting any file rows.
            migration.Execute.Sql(@"UPDATE ""Movies""
                          SET ""MovieFileId"" = COALESCE((SELECT e.""MovieFileId"" FROM ""MovieEditions"" e
                                                         WHERE e.""Id"" = ""Movies"".""PrimaryEditionId""), 0)");
        }
    }
}
