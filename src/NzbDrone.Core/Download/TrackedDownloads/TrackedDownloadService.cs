using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        TrackedDownload Find(int downloadClientId, string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(int downloadClientId, string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<MovieGrabbedEvent>,
                                          IHandle<MovieAddedEvent>,
                                          IHandle<MovieEditedEvent>,
                                          IHandle<MoviesBulkEditedEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IMovieEditionRecoveryService _editionRecovery;

        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      IHistoryService historyService,
                                      IDownloadHistoryService downloadHistoryService,
                                      IRemoteMovieAggregationService aggregationService,
                                      ICustomFormatCalculationService formatCalculator,
                                      IEventAggregator eventAggregator,
                                      ICacheManager cacheManager,
                                      IMovieEditionRecoveryService editionRecovery,
                                      Logger logger)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _editionRecovery = editionRecovery;

            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
        }

        private static string CacheKey(int clientId, string downloadId) => $"{clientId}:{downloadId}";

        public TrackedDownload Find(int downloadClientId, string downloadId)
        {
            return _cache.Find(CacheKey(downloadClientId, downloadId));
        }

        public TrackedDownload Find(string downloadId)
        {
            var matches = _cache.Values.Where(t => t.DownloadItem.DownloadId == downloadId).ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException("Download ID exists on multiple clients. Select a client-specific queue item.");
            }

            return matches.SingleOrDefault();
        }

        public void StopTracking(int downloadClientId, string downloadId)
        {
            var tracked = Find(downloadClientId, downloadId);
            if (tracked != null)
            {
                _cache.Remove(CacheKey(downloadClientId, downloadId));
                _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { tracked }));
            }
        }

        public void StopTracking(string downloadId)
        {
            var tracked = Find(downloadId);
            if (tracked != null)
            {
                StopTracking(tracked.DownloadClient, downloadId);
            }
        }

        public void StopTracking(List<string> downloadIds)
        {
            // Resolve the entire request before changing the cache; ambiguity must not partially remove work.
            var tracked = downloadIds.Distinct().Select(Find).Where(t => t != null).ToList();
            foreach (var item in tracked)
            {
                _cache.Remove(CacheKey(item.DownloadClient, item.DownloadItem.DownloadId));
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(tracked));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            return TrackDownload(downloadClient, downloadItem, false);
        }

        private TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem, bool refresh)
        {
            var existingItem = Find(downloadClient.Id, downloadItem.DownloadId);

            if (!refresh && existingItem != null && existingItem.State != TrackedDownloadState.Downloading &&
                existingItem.RemoteMovie?.EditionContext == null && existingItem.EditionRecoveryError == null)
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false
            };

            try
            {
                var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItem(downloadClient.Id, downloadItem.DownloadId);

                if (downloadHistory != null)
                {
                    var state = GetStateFromHistory(downloadHistory.EventType);
                    trackedDownload.State = state;
                }

                var historyItems = _historyService.FindByDownloadId(downloadClient.Id, downloadItem.DownloadId).OrderByDescending(h => h.Date).ToList();
                var grabbedEvent = historyItems.FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed);
                var downloadGrab = _downloadHistoryService.GetLatestGrab(downloadClient.Id, downloadItem.DownloadId) ??
                    (downloadHistory?.EventType == DownloadHistoryEventType.DownloadGrabbed ? downloadHistory : null);
                if (downloadGrab == null && downloadHistory == null &&
                    _historyService.FindByDownloadId(downloadItem.DownloadId)?.Any(h => h.MovieEditionId.HasValue) == true)
                {
                    trackedDownload.EditionRecoveryError = "Typed grab belongs to another download client; manual review required.";
                    trackedDownload.State = TrackedDownloadState.ImportBlocked;
                    trackedDownload.Warn(trackedDownload.EditionRecoveryError);
                    _cache.Set(CacheKey(downloadClient.Id, downloadItem.DownloadId), trackedDownload);
                    return trackedDownload;
                }

                if (downloadGrab?.MovieEditionId != null &&
                    (downloadGrab.DownloadClientId != downloadClient.Id ||
                     (downloadHistory != null && (downloadHistory.DownloadClientId != downloadClient.Id ||
                         downloadHistory.MovieId != downloadGrab.MovieId ||
                         (downloadHistory.MovieEditionId.HasValue && downloadHistory.MovieEditionId != downloadGrab.MovieEditionId)))))
                {
                    trackedDownload.EditionRecoveryError = "Persisted edition history is inconsistent or belongs to another download client; manual review required.";
                    trackedDownload.State = TrackedDownloadState.ImportBlocked;
                    trackedDownload.Warn(trackedDownload.EditionRecoveryError);
                    _cache.Set(CacheKey(downloadClient.Id, downloadItem.DownloadId), trackedDownload);
                    return trackedDownload;
                }

                int? assignedMovieId = grabbedEvent?.MovieEditionId != null ? grabbedEvent.MovieId :
                    downloadGrab?.MovieEditionId != null ? downloadGrab.MovieId : null;
                var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title);
                var clientEvidence = parsedMovieInfo;

                if (parsedMovieInfo != null)
                {
                    trackedDownload.RemoteMovie = assignedMovieId.HasValue
                        ? _parsingService.Map(parsedMovieInfo, assignedMovieId.Value)
                        : downloadHistory is { EventType: DownloadHistoryEventType.DownloadImported }
                        ? _parsingService.Map(parsedMovieInfo, downloadHistory.MovieId)
                        : _parsingService.Map(parsedMovieInfo, "", 0, null);
                }

                if (historyItems.Any())
                {
                    var firstHistoryItem = historyItems.First();

                    trackedDownload.Indexer = grabbedEvent?.Data?.GetValueOrDefault("indexer");
                    trackedDownload.Added = grabbedEvent?.Date;

                    if (parsedMovieInfo == null ||
                        trackedDownload.RemoteMovie?.Movie == null)
                    {
                        parsedMovieInfo = Parser.Parser.ParseMovieTitle(grabbedEvent?.SourceTitle ?? firstHistoryItem.SourceTitle);

                        if (parsedMovieInfo != null)
                        {
                            trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo,
                                assignedMovieId ?? firstHistoryItem.MovieId);
                        }
                    }

                    if (trackedDownload.RemoteMovie != null)
                    {
                        trackedDownload.RemoteMovie.Release ??= new ReleaseInfo();
                        trackedDownload.RemoteMovie.Release.Indexer = trackedDownload.Indexer;
                        trackedDownload.RemoteMovie.Release.Title = trackedDownload.RemoteMovie.ParsedMovieInfo?.ReleaseTitle;

                        if (Enum.TryParse(grabbedEvent?.Data?.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            trackedDownload.RemoteMovie.Release.IndexerFlags = flags;
                        }

                        if (downloadHistory != null)
                        {
                            trackedDownload.RemoteMovie.Release.IndexerId = downloadHistory.IndexerId;
                        }
                    }
                }

                if (trackedDownload.RemoteMovie?.Movie == null && downloadGrab != null)
                {
                    var original = Parser.Parser.ParseMovieTitle(downloadGrab.SourceTitle);
                    if (original != null)
                    {
                        trackedDownload.RemoteMovie = _parsingService.Map(original, downloadGrab.MovieId);
                    }
                }

                if (trackedDownload.RemoteMovie?.Movie != null)
                {
                    if (downloadGrab?.MovieEditionId != null && downloadGrab.Release != null)
                    {
                        // Queue deduplication needs the original indexer GUID/hash after restart.
                        trackedDownload.RemoteMovie.Release = downloadGrab.Release;
                        trackedDownload.RemoteMovie.Release.IndexerId = downloadGrab.IndexerId;
                        trackedDownload.RemoteMovie.Release.DownloadProtocol = downloadGrab.Protocol;
                    }

                    try
                    {
                        trackedDownload.RemoteMovie.EditionContext = _editionRecovery.Recover(trackedDownload.RemoteMovie.Movie, grabbedEvent, downloadGrab);
                        _editionRecovery.ValidateEvidence(trackedDownload.RemoteMovie.EditionContext, clientEvidence);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Edition recovery failed for {0}", downloadItem.DownloadId);
                        if (trackedDownload.State is TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.Ignored)
                        {
                            // Historical completion survives later target removal; never turn
                            // a terminal download back into an import candidate.
                            trackedDownload.RemoteMovie.EditionContext = null;
                        }
                        else
                        {
                            trackedDownload.EditionRecoveryError = ex.Message;
                            trackedDownload.RemoteMovie.EditionRecoveryError = ex.Message;
                            trackedDownload.State = TrackedDownloadState.ImportBlocked;
                            trackedDownload.Warn(ex.Message);
                        }
                    }

                    _aggregationService.Augment(trackedDownload.RemoteMovie);

                    // Calculate custom formats
                    trackedDownload.RemoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(trackedDownload.RemoteMovie, downloadItem.TotalSize);
                    trackedDownload.RemoteMovie.CustomFormatScore = trackedDownload.RemoteMovie.EffectiveQualityProfile?.CalculateCustomFormatScore(trackedDownload.RemoteMovie.CustomFormats) ?? 0;
                    if (existingItem != null && trackedDownload.EditionRecoveryError == null &&
                        (downloadHistory == null || downloadHistory.EventType == DownloadHistoryEventType.DownloadGrabbed))
                    {
                        trackedDownload.State = existingItem.State;
                    }
                }

                // Track it so it can be displayed in the queue even though we can't determine which movie it is for
                if (trackedDownload.RemoteMovie == null)
                {
                    _logger.Trace("No Movie found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (MultipleMoviesFoundException e)
            {
                _logger.Debug(e, "Found multiple movies for " + downloadItem.Title);

                trackedDownload.Warn("Unable to import automatically, found multiple movies: {0}", string.Join(", ", e.Movies));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find movie for " + downloadItem.Title);

                trackedDownload.Warn("Unable to parse movie from title");
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(CacheKey(trackedDownload.DownloadClient, trackedDownload.DownloadItem.DownloadId), trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().Where(t => !trackedDownloads.Any(active => active.DownloadClient == t.DownloadClient && active.DownloadItem.DownloadId == t.DownloadItem.DownloadId)).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} RadarrStage={4} Movie='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteMovie?.ParsedMovieInfo,
                    downloadItem.OutputPath);
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var refreshed = TrackDownload(new DownloadClientDefinition { Id = trackedDownload.DownloadClient, Protocol = trackedDownload.Protocol }, trackedDownload.DownloadItem, true);
            trackedDownload.RemoteMovie = refreshed.RemoteMovie;
            trackedDownload.EditionRecoveryError = refreshed.EditionRecoveryError;
            trackedDownload.State = refreshed.State;
            if (refreshed.EditionRecoveryError != null)
            {
                trackedDownload.Warn(refreshed.EditionRecoveryError);
            }

            _cache.Set(CacheKey(trackedDownload.DownloadClient, trackedDownload.DownloadItem.DownloadId), trackedDownload);
        }

        private static TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImported:
                    return TrackedDownloadState.Imported;
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.Failed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        public void Handle(MovieGrabbedEvent message)
        {
            if (message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var trackedDownload = Find(message.DownloadClientId, message.DownloadId);

            if (trackedDownload is { State: TrackedDownloadState.Imported or
                                            TrackedDownloadState.Failed or
                                            TrackedDownloadState.Ignored })
            {
                _cache.Remove(CacheKey(message.DownloadClientId, message.DownloadId));
            }
        }

        public void Handle(MovieAddedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie == null ||
                    message.Movie?.TmdbId == t.RemoteMovie.Movie.TmdbId)
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MovieEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    (t.RemoteMovie.Movie.Id == message.Movie?.Id || t.RemoteMovie.Movie.TmdbId == message.Movie?.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesBulkEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesDeletedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
