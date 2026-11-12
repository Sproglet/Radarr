using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    public class edition_submission_journalFixture : MigrationTest<edition_submission_journal>
    {
        [Test]
        public void migration_does_not_invent_submission_intent_for_existing_grabs()
        {
            using var db = WithDapperMigrationTestDb(c => c.Execute.Sql(@"INSERT INTO ""History""
                (""MovieId"",""SourceTitle"",""Quality"",""Date"",""EventType"",""Data"",""Languages"")
                VALUES (42,'Synthetic.Voyage.2026-SyntheticGroup','{}','2026-01-02 00:00:00',1,'{}','[]')"));

            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionSubmissions""").Should().Be(0);
            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""History"" WHERE ""EditionOperationId"" IS NULL AND ""MovieId""=42").Should().Be(1);
        }
    }
}
