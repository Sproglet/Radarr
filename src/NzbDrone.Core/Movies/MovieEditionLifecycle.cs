using System.Data;
using Dapper;

namespace NzbDrone.Core.Movies
{
    // Shared SQL for runtime creation and conservative repair. Never classifies filenames or removes files.
    internal static class MovieEditionLifecycle
    {
        public static void CreateDefault(IDbConnection connection, IDbTransaction transaction, int movieId)
        {
            connection.Execute(
                @"INSERT INTO ""MovieEditions""
                (""MovieId"", ""EditionKey"", ""Name"", ""Monitored"", ""QualityProfileId"", ""MovieFileId"", ""LastSearchTime"")
                SELECT m.""Id"", 'legacy-primary', COALESCE(NULLIF(TRIM(f.""Edition""), ''), 'Theatrical'),
                       m.""Monitored"", m.""QualityProfileId"", f.""Id"", m.""LastSearchTime""
                FROM ""Movies"" m LEFT JOIN ""MovieFiles"" f
                  ON f.""Id"" = m.""MovieFileId"" AND f.""MovieId"" = m.""Id""
                  AND f.""MovieEditionId"" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM ""MovieEditions"" e WHERE e.""MovieFileId"" = f.""Id"")
                WHERE m.""Id"" = @MovieId AND NOT EXISTS
                  (SELECT 1 FROM ""MovieEditions"" e WHERE e.""MovieId"" = m.""Id"" AND e.""EditionKey"" = 'legacy-primary')",
                new { MovieId = movieId },
                transaction);
            connection.Execute(
                @"UPDATE ""Movies"" SET ""PrimaryEditionId"" =
                (SELECT ""Id"" FROM ""MovieEditions"" WHERE ""MovieId"" = @MovieId AND ""EditionKey"" = 'legacy-primary')
                WHERE ""Id"" = @MovieId",
                new { MovieId = movieId },
                transaction);
            connection.Execute(
                @"UPDATE ""MovieFiles"" SET ""MovieEditionId"" =
                (SELECT ""PrimaryEditionId"" FROM ""Movies"" WHERE ""Id"" = @MovieId)
                WHERE ""MovieId"" = @MovieId AND ""Id"" =
                (SELECT ""MovieFileId"" FROM ""MovieEditions"" WHERE ""MovieId"" = @MovieId AND ""EditionKey"" = 'legacy-primary')
                AND ""MovieEditionId"" IS NULL",
                new { MovieId = movieId },
                transaction);
            Project(connection, transaction, movieId);
        }

        public static void Project(IDbConnection connection, IDbTransaction transaction, int? movieId = null)
        {
            connection.Execute(
                @"UPDATE ""Movies"" SET ""MovieFileId"" = COALESCE(
                (SELECT e.""MovieFileId"" FROM ""MovieEditions"" e
                 WHERE e.""Id"" = ""Movies"".""PrimaryEditionId"" AND e.""MovieId"" = ""Movies"".""Id""), 0)
                WHERE (@MovieId IS NULL OR ""Id"" = @MovieId)",
                new { MovieId = movieId },
                transaction);
        }

        public static void RemoveOrphanedEditions(IDbConnection connection, IDbTransaction transaction)
        {
            connection.Execute(
                @"DELETE FROM ""MovieEditions"" WHERE NOT EXISTS
                (SELECT 1 FROM ""Movies"" m WHERE m.""Id"" = ""MovieEditions"".""MovieId"")",
                transaction: transaction);
            connection.Execute(
                @"UPDATE ""MovieFiles"" SET ""MovieEditionId"" = NULL WHERE ""MovieEditionId"" IS NOT NULL AND NOT EXISTS
                (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = ""MovieFiles"".""MovieEditionId""
                 AND e.""MovieId"" = ""MovieFiles"".""MovieId"")",
                transaction: transaction);
        }

        public static void Repair(IDbConnection connection, IDbTransaction transaction)
        {
            // Maintenance takes the same parent locks as assignments before examining either direction.
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored""", transaction: transaction);
            RemoveOrphanedEditions(connection, transaction);
            connection.Execute(
                @"UPDATE ""MovieEditions"" SET ""MovieFileId"" = NULL WHERE ""MovieFileId"" IS NOT NULL AND NOT EXISTS
                (SELECT 1 FROM ""MovieFiles"" f WHERE f.""Id"" = ""MovieEditions"".""MovieFileId""
                 AND f.""MovieId"" = ""MovieEditions"".""MovieId"" AND f.""MovieEditionId"" = ""MovieEditions"".""Id"")",
                transaction: transaction);
            var missing = connection.Query<int>(
                @"SELECT ""Id"" FROM ""Movies"" WHERE NOT EXISTS
                (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = ""Movies"".""PrimaryEditionId""
                 AND e.""MovieId"" = ""Movies"".""Id"")",
                transaction: transaction).AsList();
            foreach (var movieId in missing)
            {
                CreateDefault(connection, transaction, movieId);
            }

            Project(connection, transaction);
        }
    }
}
