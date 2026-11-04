using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryRepository : IBasicRepository<DownloadHistory>
    {
        List<DownloadHistory> FindByDownloadId(string downloadId);
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
            if (history.EventType != DownloadHistoryEventType.DownloadGrabbed || !history.MovieEditionId.HasValue)
            {
                return;
            }

            connection.Execute(@"UPDATE ""Movies"" SET ""Monitored"" = ""Monitored"" WHERE ""Id"" = @MovieId", new { history.MovieId }, transaction);
            if (connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditions"" WHERE ""Id"" = @MovieEditionId AND ""MovieId"" = @MovieId",
                new { history.MovieId, history.MovieEditionId }, transaction) != 1)
            {
                throw new InvalidOperationException("Grab edition must belong to its movie.");
            }
        }

        public List<DownloadHistory> FindByDownloadId(string downloadId)
        {
            return Query(x => x.DownloadId == downloadId).OrderByDescending(h => h.Date).ToList();
        }

        public void DeleteByMovieIds(List<int> movieIds)
        {
            Delete(r => movieIds.Contains(r.MovieId));
        }
    }
}
