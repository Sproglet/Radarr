using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryService
    {
        bool DownloadAlreadyImported(string downloadId);
        DownloadHistory GetLatestDownloadHistoryItem(string downloadId);
        DownloadHistory GetLatestGrab(string downloadId);
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
            var events = _repository.FindByDownloadId(downloadId);

            // Events are ordered by date descending, if a grabbed event comes before an imported event then it was never imported
            // or grabbed again after importing and should be reprocessed.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return false;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return true;
                }
            }

            return false;
        }

        public DownloadHistory GetLatestDownloadHistoryItem(string downloadId)
        {
            var events = _repository.FindByDownloadId(downloadId);

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
            return _repository.FindByDownloadId(downloadId)
                              .FirstOrDefault(d => d.EventType == DownloadHistoryEventType.DownloadGrabbed);
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
            var grab = history.DownloadId.IsNullOrWhiteSpace() ? null : GetLatestGrab(history.DownloadId);
            if (grab != null && grab.MovieId == history.MovieId && grab.DownloadClientId == history.DownloadClientId)
            {
                history.MovieEditionId = grab.MovieEditionId;
                history.EditionRuleRevision = grab.EditionRuleRevision;
                if (grab.Data != null && grab.Data.TryGetValue("movieEditionName", out var name))
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
            var grab = message.DownloadId.IsNullOrWhiteSpace() ? null : GetLatestGrab(message.DownloadId);
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
