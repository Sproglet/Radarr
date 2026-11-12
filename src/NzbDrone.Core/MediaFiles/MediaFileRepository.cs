using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileRepository : IBasicRepository<MovieFile>
    {
        List<MovieFile> GetFilesByMovie(int movieId);
        List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds);
        List<MovieFile> GetFilesWithoutMediaInfo();
        void DeleteForMovies(List<int> movieIds);

        List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath);
    }

    public class MediaFileRepository : BasicRepository<MovieFile>, IMediaFileRepository
    {
        public MediaFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, MovieFile file)
        {
            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId", new { file.MovieId }, transaction);
            if (file.EditionContext != null)
            {
                if (file.EditionContext.Edition.MovieId != file.MovieId || file.MovieEditionId != file.EditionContext.Edition.Id)
                {
                    throw new InvalidOperationException("The file and its edition context must belong to the same movie.");
                }

                MovieEditionRepository.AssignCurrentFile(
                    connection,
                    transaction,
                    file.MovieId,
                    file.MovieEditionId.Value,
                    file.Id,
                    file.EditionContext.Edition.MovieFileId);
                return;
            }

            var primary = connection.QuerySingleOrDefault<MovieEdition>(
                @"SELECT e.* FROM ""MovieEditions"" e
                JOIN ""Movies"" m ON m.""PrimaryEditionId"" = e.""Id"" AND m.""Id"" = e.""MovieId""
                WHERE m.""Id"" = @MovieId",
                new { file.MovieId },
                transaction);
            if (primary == null || (file.MovieEditionId.HasValue && file.MovieEditionId != primary.Id))
            {
                throw new InvalidOperationException("Legacy imports require the movie's primary edition. Multi-edition import is not enabled.");
            }

            MovieEditionRepository.AssignCurrentFile(connection, transaction, file.MovieId, primary.Id, file.Id, primary.MovieFileId);
            file.MovieEditionId = primary.Id;
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, MovieFile file, List<string> properties)
        {
            connection.Execute(
                @"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" =
                (SELECT ""MovieId"" FROM ""MovieFiles"" WHERE ""Id"" = @Id)",
                new { file.Id },
                transaction);
            var stored = connection.QuerySingle<MovieFile>(@"SELECT * FROM ""MovieFiles"" WHERE ""Id"" = @Id", new { file.Id }, transaction);
            if (stored.MovieEditionId.HasValue && file.MovieId != stored.MovieId && properties.Contains(nameof(MovieFile.MovieId)))
            {
                throw new InvalidOperationException("An edition-owned file cannot be moved to another movie by a metadata update.");
            }

            // Ownership changes go through the validated edition assignment operation.
            file.MovieEditionId = stored.MovieEditionId;
        }

        protected override void OnDeleted(IDbConnection connection, IDbTransaction transaction)
        {
            connection.Execute(
                @"UPDATE ""MovieEditions"" SET ""MovieFileId"" = NULL WHERE ""MovieFileId"" IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM ""MovieFiles"" f WHERE f.""Id"" = ""MovieEditions"".""MovieFileId"")",
                transaction: transaction);
            connection.Execute(
                @"UPDATE ""Movies"" SET ""MovieFileId"" = 0 WHERE ""MovieFileId"" <> 0
                AND NOT EXISTS (SELECT 1 FROM ""MovieFiles"" f WHERE f.""Id"" = ""Movies"".""MovieFileId"")",
                transaction: transaction);
        }

        public List<MovieFile> GetFilesByMovie(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds)
        {
            return Query(x => movieIds.Contains(x.MovieId));
        }

        public List<MovieFile> GetFilesWithoutMediaInfo()
        {
            return Query(x => x.MediaInfo == null);
        }

        public void DeleteForMovies(List<int> movieIds)
        {
            Delete(x => movieIds.Contains(x.MovieId));
        }

        public List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath)
        {
            return Query(c => c.MovieId == movieId && c.RelativePath == relativePath);
        }
    }
}
