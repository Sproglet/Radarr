using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryService
    {
        bool DownloadAlreadyImported(string downloadId);
        DownloadHistory GetLatestDownloadHistoryItem(string downloadId);
        DownloadHistory GetLatestGrab(string downloadId);
        DownloadHistory GetLatestGrab(int clientId, string downloadId);
        DownloadHistory GetLatestDownloadHistoryItem(int clientId, string downloadId);
        bool DownloadAlreadyImported(int clientId, string downloadId);
    }

    public class DownloadHistoryService : IDownloadHistoryService,
                                          IHandle<MovieGrabbedEvent>,
                                          IHandle<MovieFileImportedEvent>,
                                          IHandle<DownloadCompletedEvent>,
                                          IHandle<DownloadFailedEvent>,
                                          IHandle<DownloadIgnoredEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IDownloadHistoryRepository _repository;
        private readonly IHistoryService _historyService;

        public DownloadHistoryService(IDownloadHistoryRepository repository, IHistoryService historyService)
        {
            _repository = repository;
            _historyService = historyService;
        }

        public bool DownloadAlreadyImported(string downloadId)
        {
            return IsImported(GetEvents(downloadId));
        }

        public DownloadHistory GetLatestDownloadHistoryItem(string downloadId)
        {
            var events = GetEvents(downloadId);

            // Events are ordered by date descending. We'll return the most recent expected event.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadIgnored)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadFailed)
                {
                    return e;
                }
            }

            return null;
        }

        public DownloadHistory GetLatestGrab(string downloadId)
        {
            return LatestUnambiguousGrab(GetEvents(downloadId));
        }

        private List<DownloadHistory> GetEvents(string downloadId)
        {
            var events = _repository.FindByDownloadId(downloadId);
            if (events.Select(h => h.DownloadClientId).Distinct().Count() > 1)
            {
                throw new InvalidOperationException("Download history belongs to multiple clients. Supply the download-client ID.");
            }

            return events;
        }

        public DownloadHistory GetLatestGrab(int clientId, string downloadId)
        {
            return LatestUnambiguousGrab(_repository.FindByDownloadId(clientId, downloadId));
        }

        private static DownloadHistory LatestUnambiguousGrab(List<DownloadHistory> events)
        {
            var grabs = events.Where(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed).ToList();
            if (MovieEditionOperationIdentity.HasAmbiguousOperations(grabs.Select(h => h.EditionOperationId)))
            {
                throw new InvalidOperationException("Download history has multiple or invalid edition submission operations. Review the download manually.");
            }

            return grabs.FirstOrDefault();
        }

        public DownloadHistory GetLatestDownloadHistoryItem(int clientId, string downloadId)
        {
            return _repository.FindByDownloadId(clientId, downloadId).FirstOrDefault(h => h.EventType is
                DownloadHistoryEventType.DownloadGrabbed or DownloadHistoryEventType.DownloadImported or
                DownloadHistoryEventType.DownloadFailed or DownloadHistoryEventType.DownloadIgnored);
        }

        public bool DownloadAlreadyImported(int clientId, string downloadId)
        {
            return IsImported(_repository.FindByDownloadId(clientId, downloadId));
        }

        private static bool IsImported(List<DownloadHistory> events)
        {
            if (MovieEditionOperationIdentity.HasAmbiguousOperations(events.Where(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed).Select(h => h.EditionOperationId)))
            {
                return false;
            }

            // A newer grab restarts this download. A typed older grab can only be
            // settled by a completion retaining its saved assignment and revision.
            var latest = events.FirstOrDefault(h => h.EventType is
                DownloadHistoryEventType.DownloadGrabbed or DownloadHistoryEventType.DownloadImported);
            if (latest?.EventType != DownloadHistoryEventType.DownloadImported)
            {
                return false;
            }

            var grab = events.FirstOrDefault(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed);
            return grab?.MovieEditionId == null || (latest.MovieId == grab.MovieId &&
                latest.MovieEditionId == grab.MovieEditionId && latest.EditionRuleRevision == grab.EditionRuleRevision &&
                latest.EditionOperationId == grab.EditionOperationId);
        }

        public void Handle(MovieGrabbedEvent message)
        {
            // Don't store grabbed events for clients that don't download IDs
            if (message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                MovieId = message.Movie.Movie.Id,
                MovieEditionId = message.Movie.EditionContext?.Edition.Id,
                EditionOperationId = message.EditionOperationId,
                EditionRuleRevision = message.Movie.EditionEvaluation?.Classification?.RuleRevision ?? message.Movie.EditionContext?.Edition.RuleRevision,
                DownloadId = message.DownloadId,
                SourceTitle = message.Movie.Release.Title,
                Date = DateTime.UtcNow,
                Protocol = message.Movie.Release.DownloadProtocol,
                IndexerId = message.Movie.Release.IndexerId,
                DownloadClientId = message.DownloadClientId,
                Release = message.Movie.Release
            };

            if (message.Movie.EditionContext != null)
            {
                history.Data["movieEditionName"] = message.Movie.EditionContext.Edition.Name;
            }

            history.Data.Add("Indexer", message.Movie.Release.Indexer);
            history.Data.Add("DownloadClient", message.DownloadClient);
            history.Data.Add("DownloadClientName", message.DownloadClientName);

            history.Data.Add("CustomFormatScore", message.Movie.CustomFormatScore.ToString());
            _repository.Insert(history);
        }

        public void Handle(MovieFileImportedEvent message)
        {
            if (!message.NewDownload)
            {
                return;
            }

            var downloadId = message.DownloadId;

            // Try to find the downloadId if the user used manual import (from wanted: missing) or the
            // API to import and downloadId wasn't provided.
            if (downloadId.IsNullOrWhiteSpace())
            {
                downloadId = _historyService.FindDownloadId(message);
            }

            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported,
                MovieId = message.ImportedMovie.MovieId,
                MovieEditionId = message.ImportedMovie.MovieEditionId,
                EditionRuleRevision = message.MovieInfo.EditionContext?.Edition.RuleRevision,
                DownloadId = downloadId,
                SourceTitle = message.MovieInfo.Path,
                Date = DateTime.UtcNow,
                Protocol = message.DownloadClientInfo.Protocol,
                DownloadClientId = message.DownloadClientInfo.Id
            };

            history.Data.Add("DownloadClient", message.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", message.DownloadClientInfo.Name);

            _repository.Insert(history);
        }

        private void CopyGrabIdentity(DownloadHistory history, RemoteMovie remoteMovie)
        {
            var grab = history.DownloadId.IsNullOrWhiteSpace() ? null : GetLatestGrab(history.DownloadClientId, history.DownloadId);
            var movieGrabs = history.DownloadId.IsNullOrWhiteSpace() ? new List<MovieHistory>() :
                (_historyService.FindByDownloadId(history.DownloadClientId, history.DownloadId) ?? new List<MovieHistory>())
                    .Where(h => h.EventType == MovieHistoryEventType.Grabbed).OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToList();
            if (MovieEditionOperationIdentity.HasAmbiguousOperations(movieGrabs.Select(h => h.EditionOperationId)))
            {
                throw new InvalidOperationException("Movie history has multiple or invalid edition submission operations. Review the download manually.");
            }

            var movieGrab = movieGrabs.FirstOrDefault();
            if (movieGrab?.MovieEditionId != null && (movieGrab.MovieId != history.MovieId ||
                (grab != null && (grab.MovieEditionId != movieGrab.MovieEditionId || grab.EditionRuleRevision != movieGrab.EditionRuleRevision ||
                    grab.EditionOperationId != movieGrab.EditionOperationId))))
            {
                throw new InvalidOperationException("Grab records disagree about the saved edition submission identity. Review the download manually.");
            }

            if (grab != null && grab.MovieId == history.MovieId && grab.DownloadClientId == history.DownloadClientId)
            {
                history.MovieEditionId = grab.MovieEditionId;
                history.EditionRuleRevision = grab.EditionRuleRevision;
                history.EditionOperationId = grab.EditionOperationId;
                if (grab.Data != null && grab.Data.TryGetValue("movieEditionName", out var name))
                {
                    history.Data["movieEditionName"] = name;
                }
            }
            else if (movieGrab?.MovieEditionId != null)
            {
                if (!int.TryParse(movieGrab.Data?.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var clientId) || clientId != history.DownloadClientId)
                {
                    throw new InvalidOperationException("Movie-history-only settlement requires positive download-client provenance.");
                }

                history.MovieEditionId = movieGrab.MovieEditionId;
                history.EditionRuleRevision = movieGrab.EditionRuleRevision;
                history.EditionOperationId = movieGrab.EditionOperationId;
                if (movieGrab.Data.TryGetValue("movieEditionName", out var name))
                {
                    history.Data["movieEditionName"] = name;
                }
            }
            else if (remoteMovie?.EditionContext != null && remoteMovie.Movie.Id == history.MovieId)
            {
                history.MovieEditionId = remoteMovie.EditionContext.Edition.Id;
                history.EditionRuleRevision = remoteMovie.EditionContext.Edition.RuleRevision;
                history.Data["movieEditionName"] = remoteMovie.EditionContext.Edition.Name;
            }
        }

        public void Handle(DownloadCompletedEvent message)
        {
            var downloadItem = message.TrackedDownload.DownloadItem;

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImported,
                MovieId = message.MovieId,
                DownloadId = downloadItem.DownloadId,
                SourceTitle = downloadItem.Title,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", downloadItem.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", downloadItem.DownloadClientInfo.Name);

            CopyGrabIdentity(history, message.TrackedDownload?.RemoteMovie);
            _repository.Insert(history);
        }

        public void Handle(DownloadFailedEvent message)
        {
            var clientId = message.DownloadClientId ?? message.TrackedDownload?.DownloadClient;
            var grab = message.DownloadId.IsNullOrWhiteSpace() || !clientId.HasValue ? null : GetLatestGrab(clientId.Value, message.DownloadId);
            if (message.TrackedDownload == null &&
                (!clientId.HasValue || grab == null || grab.MovieId != message.MovieId || grab.DownloadClientId != clientId ||
                    grab.MovieEditionId != message.MovieEditionId))
            {
                return; // History-only failures need positive client/target provenance.
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadFailed,
                MovieId = message.MovieId,
                DownloadId = message.DownloadId,
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload?.Protocol ?? grab.Protocol,
                DownloadClientId = clientId.Value
            };

            history.Data.Add("DownloadClient", message.TrackedDownload?.DownloadItem.DownloadClientInfo.Type ?? message.DownloadClient);
            history.Data.Add("DownloadClientName", message.TrackedDownload?.DownloadItem.DownloadClientInfo.Name ?? message.Data.GetValueOrDefault("downloadClientName"));

            CopyGrabIdentity(history, message.TrackedDownload?.RemoteMovie);
            _repository.Insert(history);
        }

        public void Handle(DownloadIgnoredEvent message)
        {
            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadIgnored,
                MovieId = message.MovieId,
                DownloadId = message.DownloadId,
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = message.DownloadClientInfo.Protocol,
                DownloadClientId = message.DownloadClientInfo.Id
            };

            history.Data.Add("DownloadClient", message.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", message.DownloadClientInfo.Name);

            CopyGrabIdentity(history, message.TrackedDownload?.RemoteMovie);
            _repository.Insert(history);
        }

        public void Handle(MoviesDeletedEvent message)
        {
            _repository.DeleteByMovieIds(message.Movies.Select(m => m.Id).ToList());
        }
    }
}
