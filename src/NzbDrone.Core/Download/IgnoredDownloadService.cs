using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Download
{
    public interface IIgnoredDownloadService
    {
        bool IgnoreDownload(TrackedDownload trackedDownload);
    }

    public class IgnoredDownloadService : IIgnoredDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IDownloadHistoryService _history;
        private readonly IMovieEditionRecoveryService _recovery;

        public IgnoredDownloadService(IEventAggregator eventAggregator,
                                      IDownloadHistoryService history,
                                      IMovieEditionRecoveryService recovery,
                                      Logger logger)
        {
            _eventAggregator = eventAggregator;
            _history = history;
            _recovery = recovery;
            _logger = logger;
        }

        public bool IgnoreDownload(TrackedDownload trackedDownload)
        {
            var movie = trackedDownload.RemoteMovie?.Movie;

            if (movie == null)
            {
                _logger.Warn("Unable to ignore download for unknown movie");
                return false;
            }

            DownloadHistory grab;
            try
            {
                if (trackedDownload.EditionRecoveryError != null || trackedDownload.RemoteMovie.EditionRecoveryError != null)
                {
                    throw new InvalidOperationException("Edition recovery requires manual review before ignoring the download.");
                }

                grab = _history.GetLatestGrab(trackedDownload.DownloadItem.DownloadId);
                if (grab != null && (grab.MovieId != movie.Id || grab.DownloadClientId != trackedDownload.DownloadClient))
                {
                    throw new InvalidOperationException("Ignore grab belongs to another movie or client; manual review required.");
                }

                var context = _recovery.Recover(movie, null, grab);
                if (context?.Edition.Id != trackedDownload.RemoteMovie.EditionContext?.Edition.Id)
                {
                    throw new InvalidOperationException("Ignore target is inconsistent with its saved grab; manual review required.");
                }

                _recovery.ValidateEvidence(context, Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title));
            }
            catch (InvalidOperationException ex)
            {
                trackedDownload.EditionRecoveryError = ex.Message;
                trackedDownload.RemoteMovie.EditionRecoveryError = ex.Message;
                trackedDownload.State = TrackedDownloadState.ImportBlocked;
                trackedDownload.Warn(ex.Message);
                return false;
            }

            var downloadIgnoredEvent = new DownloadIgnoredEvent
            {
                MovieId = movie.Id,
                MovieEditionId = grab?.MovieEditionId,
                EditionRuleRevision = grab?.EditionRuleRevision,
                MovieEditionName = grab?.Data?.GetValueOrDefault("movieEditionName"),
                Languages = trackedDownload.RemoteMovie.Languages,
                Quality = trackedDownload.RemoteMovie.ParsedMovieInfo.Quality,
                SourceTitle = trackedDownload.DownloadItem.Title,
                DownloadClientInfo = trackedDownload.DownloadItem.DownloadClientInfo,
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                TrackedDownload = trackedDownload,
                Message = "Manually ignored"
            };

            _eventAggregator.PublishEvent(downloadIgnoredEvent);
            return true;
        }
    }
}
