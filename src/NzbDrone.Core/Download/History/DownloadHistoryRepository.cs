using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
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
                MovieEditionGrabGuard.LockAndValidate(connection, transaction, history.MovieId, history.MovieEditionId, history.EditionOperationId);
            }
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, DownloadHistory history, List<string> properties)
        {
            var stored = connection.QuerySingle<DownloadHistory>(@"SELECT * FROM ""DownloadHistory"" WHERE ""Id"" = @Id", history, transaction);
            if (stored.MovieEditionId.HasValue && (stored.EventType == DownloadHistoryEventType.DownloadGrabbed || stored.EditionOperationId != null))
            {
                MovieEditionGrabGuard.ValidateSnapshot(
                    properties,
                    (nameof(DownloadHistory.MovieId), stored.MovieId, history.MovieId),
                    (nameof(DownloadHistory.MovieEditionId), stored.MovieEditionId, history.MovieEditionId),
                    (nameof(DownloadHistory.EditionRuleRevision), stored.EditionRuleRevision, history.EditionRuleRevision),
                    (nameof(DownloadHistory.EditionOperationId), stored.EditionOperationId, history.EditionOperationId),
                    (nameof(DownloadHistory.DownloadId), stored.DownloadId, history.DownloadId),
                    (nameof(DownloadHistory.SourceTitle), stored.SourceTitle, history.SourceTitle),
                    (nameof(DownloadHistory.Date), MovieEditionGrabGuard.TimestampMicroseconds(stored.Date), MovieEditionGrabGuard.TimestampMicroseconds(history.Date)),
                    (nameof(DownloadHistory.EventType), stored.EventType, history.EventType),
                    (nameof(DownloadHistory.DownloadClientId), stored.DownloadClientId, history.DownloadClientId),
                    (nameof(DownloadHistory.Data), stored.Data?.GetValueOrDefault("movieEditionName"), history.Data?.GetValueOrDefault("movieEditionName")));
                return;
            }

            var eventType = properties.Contains(nameof(DownloadHistory.EventType)) ? history.EventType : stored.EventType;
            if (eventType == DownloadHistoryEventType.DownloadGrabbed)
            {
                var movieId = properties.Contains(nameof(DownloadHistory.MovieId)) ? history.MovieId : stored.MovieId;
                var editionId = properties.Contains(nameof(DownloadHistory.MovieEditionId)) ? history.MovieEditionId : stored.MovieEditionId;
                var operationId = properties.Contains(nameof(DownloadHistory.EditionOperationId)) ? history.EditionOperationId : stored.EditionOperationId;
                MovieEditionGrabGuard.LockAndValidate(connection, transaction, movieId, editionId, operationId);
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
