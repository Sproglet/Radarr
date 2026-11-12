using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadAlreadyImported
    {
        bool IsImported(TrackedDownload trackedDownload, List<MovieHistory> historyItems);
    }

    public class TrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
    {
        private readonly Logger _logger;

        public TrackedDownloadAlreadyImported(Logger logger)
        {
            _logger = logger;
        }

        public bool IsImported(TrackedDownload trackedDownload, List<MovieHistory> historyItems)
        {
            _logger.Trace("Checking if all movies for '{0}' have been imported", trackedDownload.DownloadItem.Title);

            if (historyItems.Empty())
            {
                _logger.Trace("No history for {0}", trackedDownload.DownloadItem.Title);
                return false;
            }

            var movie = trackedDownload.RemoteMovie.Movie;

            var edition = trackedDownload.RemoteMovie.EditionContext?.Edition.Id;
            var lastHistoryItem = historyItems.Where(h => h.MovieId == movie.Id &&
                    (!int.TryParse(h.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID), out var client) || client == trackedDownload.DownloadClient) &&
                    (!edition.HasValue || (h.MovieEditionId == edition &&
                        h.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID) == trackedDownload.DownloadClient.ToString() &&
                        string.Equals(h.DownloadId, trackedDownload.DownloadItem.DownloadId, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).FirstOrDefault();

            if (lastHistoryItem == null)
            {
                _logger.Trace("No history for movie: {0}", movie.ToString());
                return false;
            }

            var allMoviesImportedInHistory = lastHistoryItem.EventType == MovieHistoryEventType.DownloadFolderImported;
            _logger.Trace("Last event for movie: {0} is: {1}", movie, lastHistoryItem.EventType);

            _logger.Trace("All movies for '{0}' have been imported: {1}", trackedDownload.DownloadItem.Title, allMoviesImportedInHistory);
            return allMoviesImportedInHistory;
        }
    }
}
