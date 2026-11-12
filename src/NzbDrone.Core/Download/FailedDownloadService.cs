using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IFailedDownloadService
    {
        void MarkAsFailed(int historyId, bool skipRedownload = false);
        void MarkAsFailed(TrackedDownload trackedDownload, bool skipRedownload = false);
        void Check(TrackedDownload trackedDownload);
        void ProcessFailed(TrackedDownload trackedDownload);
    }

    public class FailedDownloadService : IFailedDownloadService
    {
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDownloadHistoryService _downloadHistory;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionRecoveryService _editionRecovery;

        public FailedDownloadService(IHistoryService historyService,
                                     IEventAggregator eventAggregator,
                                     IDownloadHistoryService downloadHistory,
                                     IMovieEditionRepository editions,
                                     IMovieEditionRecoveryService editionRecovery)
        {
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _downloadHistory = downloadHistory;
            _editions = editions;
            _editionRecovery = editionRecovery;
        }

        public void MarkAsFailed(int historyId, bool skipRedownload = false)
        {
            var history = _historyService.Get(historyId);
            PublishDownloadFailedEvent(history, "Manually marked as failed", skipRedownload: skipRedownload);
        }

        public void MarkAsFailed(TrackedDownload trackedDownload, bool skipRedownload = false)
        {
            var history = GetGrabbedHistory(trackedDownload);

            if (history.Any())
            {
                PublishDownloadFailedEvent(history.First(), "Manually marked as failed", trackedDownload, skipRedownload: skipRedownload);
            }
        }

        public void Check(TrackedDownload trackedDownload)
        {
            // Only process tracked downloads that are still downloading or import is blocked (if they fail after attempting to be processed)
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            if (trackedDownload.DownloadItem.IsEncrypted ||
                trackedDownload.DownloadItem.Status == DownloadItemStatus.Failed)
            {
                var grabbedItems = GetGrabbedHistory(trackedDownload);

                if (grabbedItems.Empty())
                {
                    if (trackedDownload.EditionRecoveryError == null)
                    {
                        trackedDownload.Warn(trackedDownload.DownloadItem.IsEncrypted ? "Download is encrypted and wasn't grabbed by Radarr, skipping automatic download handling" : "Download has failed wasn't grabbed by Radarr, skipping automatic download handling");
                    }

                    return;
                }

                trackedDownload.State = TrackedDownloadState.FailedPending;
            }
        }

        public void ProcessFailed(TrackedDownload trackedDownload)
        {
            if (trackedDownload.State != TrackedDownloadState.FailedPending)
            {
                return;
            }

            var grabbedItems = GetGrabbedHistory(trackedDownload);

            if (grabbedItems.Empty())
            {
                return;
            }

            var failure = "Failed download detected";

            if (trackedDownload.DownloadItem.IsEncrypted)
            {
                failure = "Encrypted download detected";
            }
            else if (trackedDownload.DownloadItem.Status == DownloadItemStatus.Failed && trackedDownload.DownloadItem.Message.IsNotNullOrWhiteSpace())
            {
                failure = trackedDownload.DownloadItem.Message;
            }

            trackedDownload.State = TrackedDownloadState.Failed;
            PublishDownloadFailedEvent(grabbedItems.First(), failure, trackedDownload);
        }

        private void PublishDownloadFailedEvent(MovieHistory historyItem, string message, TrackedDownload trackedDownload = null, bool skipRedownload = false)
        {
            Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

            var downloadFailedEvent = new DownloadFailedEvent
            {
                MovieId = historyItem.MovieId,
                MovieEditionId = historyItem.MovieEditionId,
                EditionRuleRevision = historyItem.EditionRuleRevision,
                MovieEditionName = historyItem.Data.GetValueOrDefault("movieEditionName"),
                DownloadClientId = trackedDownload?.DownloadClient ??
                    (int.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var clientId) ? clientId : (int?)null),
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                DownloadClient = historyItem.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT),
                DownloadId = historyItem.DownloadId,
                Message = message,
                Data = new Dictionary<string, string>(historyItem.Data, StringComparer.OrdinalIgnoreCase),
                TrackedDownload = trackedDownload,
                Languages = historyItem.Languages,
                SkipRedownload = skipRedownload,
                ReleaseSource = releaseSource,
            };

            _eventAggregator.PublishEvent(downloadFailedEvent);
        }

        private List<MovieHistory> GetGrabbedHistory(TrackedDownload download)
        {
            var history = _historyService.FindByDownloadId(download.DownloadClient, download.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed)
                .OrderByDescending(h => h.Date).ToList();
            var remote = download.RemoteMovie;
            try
            {
                if (download.EditionRecoveryError != null || remote?.EditionRecoveryError != null)
                {
                    throw new InvalidOperationException("Edition recovery requires manual review before failure handling.");
                }

                if (remote?.EditionContext == null && !history.Any(h => h.MovieEditionId.HasValue))
                {
                    if (remote?.Movie != null && _editions.GetByMovie(remote.Movie.Id)?.Count > 1)
                    {
                        throw new InvalidOperationException("Unassigned multi-edition failures require manual review.");
                    }

                    return history;
                }

                if (remote?.Movie == null || remote.EditionContext == null || history.Count == 0 ||
                    history.Any(h => h.MovieId != remote.Movie.Id || h.MovieEditionId != remote.EditionContext.Edition.Id ||
                        (int.TryParse(h.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var client) && client != download.DownloadClient)))
                {
                    throw new InvalidOperationException("Failure grab history has conflicting or missing edition identity.");
                }

                var grab = _downloadHistory.GetLatestGrab(download.DownloadClient, download.DownloadItem.DownloadId);
                if (grab == null || grab.DownloadClientId != download.DownloadClient || grab.MovieId != remote.Movie.Id ||
                    grab.MovieEditionId != remote.EditionContext.Edition.Id)
                {
                    throw new InvalidOperationException("Failure grab belongs to another client or edition; manual review required.");
                }

                var recovered = _editionRecovery.Recover(remote.Movie, history[0], grab);
                if (recovered?.Edition.Id != remote.EditionContext.Edition.Id)
                {
                    throw new InvalidOperationException("Failure target could not be recovered.");
                }

                _editionRecovery.ValidateEvidence(recovered, Parser.Parser.ParseMovieTitle(download.DownloadItem.Title));
                return history;
            }
            catch (InvalidOperationException ex)
            {
                download.EditionRecoveryError = ex.Message;
                if (remote != null)
                {
                    remote.EditionRecoveryError = ex.Message;
                }

                download.State = TrackedDownloadState.ImportBlocked;
                download.Warn(ex.Message);
                return new List<MovieHistory>();
            }
        }
    }
}
