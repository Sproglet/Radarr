using System.Data;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedMovieMovieFileIds : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedMovieMovieFileIds(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            using var transaction = mapper.BeginTransaction(IsolationLevel.ReadCommitted);
            MovieEditionLifecycle.Repair(mapper, transaction);
            transaction.Commit();
        }
    }
}
