using System.Data;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedMovies : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedMovies(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            using var transaction = mapper.BeginTransaction(IsolationLevel.ReadCommitted);
            mapper.Execute(
                @"DELETE FROM ""Movies""
                             WHERE ""Id"" IN (
                             SELECT ""Movies"".""Id"" FROM ""Movies""
                             LEFT OUTER JOIN ""MovieMetadata"" ON ""Movies"".""MovieMetadataId"" = ""MovieMetadata"".""Id""
                             WHERE ""MovieMetadata"".""Id"" IS NULL)",
                transaction: transaction);
            MovieEditionLifecycle.Repair(mapper, transaction);
            transaction.Commit();
        }
    }
}
