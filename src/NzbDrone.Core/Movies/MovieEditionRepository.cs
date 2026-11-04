using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionRepository : IBasicRepository<MovieEdition>
    {
        List<MovieEdition> GetByMovie(int movieId);
        void SetPrimaryEdition(int movieId, int editionId);
        void SetCurrentFile(int movieId, int editionId, int fileId, int? expectedFileId);
    }

    public class MovieEditionRepository : BasicRepository<MovieEdition>, IMovieEditionRepository
    {
        public MovieEditionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, MovieEdition edition)
        {
            MovieEditionClassifier.ValidateRules(edition.MatchRules);
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, MovieEdition edition, List<string> properties)
        {
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" =
                (SELECT ""MovieId"" FROM ""MovieEditions"" WHERE ""Id"" = @Id)", new { edition.Id }, transaction);
            var stored = connection.QuerySingle<MovieEdition>(@"SELECT * FROM ""MovieEditions"" WHERE ""Id"" = @Id", new { edition.Id }, transaction);
            if (properties.Contains(nameof(MovieEdition.MatchRules)))
            {
                MovieEditionClassifier.ValidateRules(edition.MatchRules);
            }

            var rulesChanged = properties.Contains(nameof(MovieEdition.MatchRules)) &&
                !(edition.MatchRules ?? new List<string>()).SequenceEqual(stored.MatchRules ?? new List<string>());
            var attributesChanged = properties.Contains(nameof(MovieEdition.Attributes)) &&
                (edition.Attributes.Count != stored.Attributes.Count || edition.Attributes.Any(a => !stored.Attributes.TryGetValue(a.Key, out var value) || value != a.Value));
            edition.RuleRevision = rulesChanged || attributesChanged ? checked(stored.RuleRevision + 1) : stored.RuleRevision;
            if (rulesChanged || attributesChanged)
            {
                connection.Execute(@"UPDATE ""MovieEditions"" SET ""RuleRevision"" = @RuleRevision WHERE ""Id"" = @Id",
                    new { edition.Id, edition.RuleRevision }, transaction);
            }

            if ((properties.Contains(nameof(MovieEdition.MovieId)) && edition.MovieId != stored.MovieId) ||
                (properties.Contains(nameof(MovieEdition.MovieFileId)) && edition.MovieFileId != stored.MovieFileId))
            {
                throw new InvalidOperationException("Edition ownership and current files must be changed through validated lifecycle operations.");
            }
        }

        protected override void OnDeleted(IDbConnection connection, IDbTransaction transaction)
        {
            // Roll back removal if either the primary selection or any retained file still refers to it.
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored""", transaction: transaction);
            var references = connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""Movies"" m
                WHERE m.""PrimaryEditionId"" IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = m.""PrimaryEditionId"")", transaction: transaction);
            references += connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieFiles"" f
                WHERE f.""MovieEditionId"" IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = f.""MovieEditionId"")", transaction: transaction);
            references += connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""PendingReleases"" p
                WHERE p.""MovieEditionId"" IS NOT NULL AND NOT EXISTS
                    (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = p.""MovieEditionId"")", transaction: transaction);
            references += connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""DownloadHistory"" h
                WHERE h.""MovieEditionId"" IS NOT NULL AND h.""EventType"" = 1
                  AND NOT EXISTS (SELECT 1 FROM ""MovieEditions"" e WHERE e.""Id"" = h.""MovieEditionId"")
                  AND NOT EXISTS (SELECT 1 FROM ""DownloadHistory"" d
                      WHERE d.""DownloadId"" = h.""DownloadId"" AND d.""DownloadClientId"" = h.""DownloadClientId""
                        AND d.""EventType"" IN (2, 3, 4)
                        AND (d.""Date"" > h.""Date"" OR (d.""Date"" = h.""Date"" AND d.""Id"" > h.""Id"")))", transaction: transaction);
            if (references != 0)
            {
                throw new InvalidOperationException("Cannot remove a primary edition, an edition that still owns files, or a target with pending/in-flight work.");
            }
        }

        public List<MovieEdition> GetByMovie(int movieId)
        {
            return Query(e => e.MovieId == movieId);
        }

        public void SetPrimaryEdition(int movieId, int editionId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId", new { MovieId = movieId }, transaction);
            var updated = connection.Execute(@"UPDATE ""Movies"" SET ""PrimaryEditionId"" = @EditionId,
                ""MovieFileId"" = COALESCE((SELECT ""MovieFileId"" FROM ""MovieEditions"" WHERE ""Id"" = @EditionId), 0)
                WHERE ""Id"" = @MovieId AND EXISTS (SELECT 1 FROM ""MovieEditions"" e
                    WHERE e.""Id"" = @EditionId AND e.""MovieId"" = @MovieId AND
                    (e.""MovieFileId"" IS NULL OR EXISTS (SELECT 1 FROM ""MovieFiles"" f
                      WHERE f.""Id"" = e.""MovieFileId"" AND f.""MovieId"" = @MovieId AND f.""MovieEditionId"" = e.""Id"")))",
                new { MovieId = movieId, EditionId = editionId }, transaction);
            if (updated != 1)
            {
                throw new InvalidOperationException("The primary edition must belong to the movie and have valid file ownership.");
            }

            transaction.Commit();
        }

        // Storage primitive only: callers must finish filesystem and import validation first.
        // ExpectedFileId prevents a stale import from replacing a newer database assignment.
        public void SetCurrentFile(int movieId, int editionId, int fileId, int? expectedFileId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            AssignCurrentFile(connection, transaction, movieId, editionId, fileId, expectedFileId);
            transaction.Commit();
        }

        internal static void AssignCurrentFile(IDbConnection connection, IDbTransaction transaction, int movieId, int editionId, int fileId, int? expectedFileId)
        {
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId", new { MovieId = movieId }, transaction);
            var parameters = new { MovieId = movieId, EditionId = editionId, FileId = fileId, ExpectedFileId = expectedFileId };

            var claimed = connection.Execute(@"UPDATE ""MovieFiles""
                SET ""MovieEditionId"" = @EditionId
                WHERE ""Id"" = @FileId AND ""MovieId"" = @MovieId
                  AND (""MovieEditionId"" IS NULL OR ""MovieEditionId"" = @EditionId)
                  AND EXISTS (SELECT 1 FROM ""Movies"" WHERE ""Id"" = @MovieId)
                  AND EXISTS (SELECT 1 FROM ""MovieEditions"" WHERE ""Id"" = @EditionId AND ""MovieId"" = @MovieId)
                  AND NOT EXISTS (SELECT 1 FROM ""MovieEditions"" WHERE ""MovieFileId"" = @FileId AND ""Id"" <> @EditionId)",
                parameters, transaction);

            if (claimed != 1)
            {
                throw new InvalidOperationException("The movie file must belong to the target movie and cannot belong to another edition.");
            }

            var updated = connection.Execute(@"UPDATE ""MovieEditions""
                SET ""MovieFileId"" = @FileId
                WHERE ""Id"" = @EditionId AND ""MovieId"" = @MovieId
                  AND (""MovieFileId"" = @ExpectedFileId OR (""MovieFileId"" IS NULL AND @ExpectedFileId IS NULL))",
                parameters, transaction);

            if (updated != 1)
            {
                throw new InvalidOperationException("The edition's current file changed. Reload the edition before assigning a file.");
            }

            connection.Execute(@"UPDATE ""Movies"" SET ""MovieFileId"" = @FileId
                WHERE ""Id"" = @MovieId AND ""PrimaryEditionId"" = @EditionId", parameters, transaction);
        }
    }
}
