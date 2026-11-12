using System.Collections.Generic;
using System.Data;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryRepository : IBasicRepository<DownloadHistory>
    {
        List<DownloadHistory> FindByDownloadId(string downloadId);
        List<DownloadHistory> FindByDownloadId(int clientId, string downloadId);
        void DeleteByMovieIds(List<int> movieIds);
    }

    public class DownloadHistoryRepository : BasicRepository<DownloadHistory>, IDownloadHistoryRepository
    {
        public DownloadHistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, DownloadHistory history)
        {
            if (history.EventType == DownloadHistoryEventType.DownloadGrabbed)
            {
                MovieEditionGrabGuard.LockAndValidate(connection, transaction, history.MovieId, history.MovieEditionId);
            }
        }

        public List<DownloadHistory> FindByDownloadId(string downloadId)
        {
            return Query(x => x.DownloadId == downloadId).OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToList();
        }

        public List<DownloadHistory> FindByDownloadId(int clientId, string downloadId)
        {
            return Query(x => x.DownloadId == downloadId && x.DownloadClientId == clientId)
                .OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToList();
        }

        public void DeleteByMovieIds(List<int> movieIds)
        {
            Delete(r => movieIds.Contains(r.MovieId));
        }
    }
}
