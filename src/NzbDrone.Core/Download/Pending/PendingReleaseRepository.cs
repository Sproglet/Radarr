using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Download.Pending
{
    public interface IPendingReleaseRepository : IBasicRepository<PendingRelease>
    {
        void DeleteByMovieIds(List<int> movieIds);
        List<PendingRelease> AllByMovieId(int movieId);
        List<PendingRelease> AllByEdition(int movieId, int? editionId);
        List<PendingRelease> WithoutFallback();
    }

    public class PendingReleaseRepository : BasicRepository<PendingRelease>, IPendingReleaseRepository
    {
        public PendingReleaseRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, PendingRelease release)
        {
            ValidateTarget(connection, transaction, release);
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, PendingRelease release, List<string> properties)
        {
            connection.Execute(
                @"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" =
                (SELECT ""MovieId"" FROM ""PendingReleases"" WHERE ""Id"" = @Id)",
                new { release.Id },
                transaction);
            var stored = connection.QuerySingle<PendingRelease>(@"SELECT * FROM ""PendingReleases"" WHERE ""Id"" = @Id", new { release.Id }, transaction);
            if ((properties.Contains(nameof(PendingRelease.MovieId)) && release.MovieId != stored.MovieId) ||
                (properties.Contains(nameof(PendingRelease.MovieEditionId)) && release.MovieEditionId != stored.MovieEditionId))
            {
                throw new InvalidOperationException("Pending ownership cannot be reassigned by a metadata update.");
            }

            ValidateTarget(connection, transaction, stored);
        }

        private static void ValidateTarget(IDbConnection connection, IDbTransaction transaction, PendingRelease release)
        {
            if (!release.MovieEditionId.HasValue)
            {
                return;
            }

            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId", new { release.MovieId }, transaction);
            if (connection.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM ""MovieEditions"" WHERE ""Id"" = @MovieEditionId AND ""MovieId"" = @MovieId",
                new { release.MovieId, release.MovieEditionId },
                transaction) != 1)
            {
                throw new InvalidOperationException("Pending edition must belong to its movie.");
            }
        }

        public void DeleteByMovieIds(List<int> movieIds)
        {
            Delete(x => movieIds.Contains(x.MovieId));
        }

        public List<PendingRelease> AllByMovieId(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<PendingRelease> AllByEdition(int movieId, int? editionId)
        {
            return Query(x => x.MovieId == movieId && x.MovieEditionId == editionId);
        }

        public List<PendingRelease> WithoutFallback()
        {
            var builder = new SqlBuilder(_database.DatabaseType)
                .InnerJoin<PendingRelease, Movie>((p, m) => p.MovieId == m.Id)
                .Where<PendingRelease>(p => p.Reason != PendingReleaseReason.Fallback);

            return Query(builder);
        }
    }
}
