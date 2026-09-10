using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionRepository : IBasicRepository<MovieEdition>
    {
        List<MovieEdition> GetByMovie(int movieId);
        void SetCurrentFile(int movieId, int editionId, int fileId, int? expectedFileId);
    }

    public class MovieEditionRepository : BasicRepository<MovieEdition>, IMovieEditionRepository
    {
        public MovieEditionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieEdition> GetByMovie(int movieId)
        {
            return Query(e => e.MovieId == movieId);
        }

        // Storage primitive only: callers must finish filesystem and import validation first.
        // ExpectedFileId prevents a stale import from replacing a newer database assignment.
        public void SetCurrentFile(int movieId, int editionId, int fileId, int? expectedFileId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
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

            transaction.Commit();
        }
    }
}
