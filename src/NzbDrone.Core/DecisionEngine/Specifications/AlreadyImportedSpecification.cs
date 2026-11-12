using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class AlreadyImportedSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly IHistoryService _historyService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly IMovieEditionRepository _editions;

        public AlreadyImportedSpecification(IHistoryService historyService,
                                            IConfigService configService,
                                            IMovieEditionRepository editions,
                                            Logger logger)
        {
            _historyService = historyService;
            _configService = configService;
            _editions = editions;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Database;
        public RejectionType Type => RejectionType.Permanent;

        public DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, ReleaseDecisionInformation information)
        {
            var cdhEnabled = _configService.EnableCompletedDownloadHandling;

            if (!cdhEnabled)
            {
                _logger.Debug("Skipping already imported check because CDH is disabled");
                return DownloadSpecDecision.Accept();
            }

            var movie = subject.Movie;

            _logger.Debug("Performing already imported check on report");
            if (movie != null)
            {
                if (subject.EditionContext != null)
                {
                    return CheckEdition(subject);
                }

                if (!movie.HasFile)
                {
                    _logger.Debug("Skipping already imported check for movie without file");
                    return DownloadSpecDecision.Accept();
                }

                var historyForMovie = _historyService.GetByMovieId(movie.Id, null);
                var lastGrabbed = historyForMovie.FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed);

                if (lastGrabbed == null)
                {
                    return DownloadSpecDecision.Accept();
                }

                var imported = historyForMovie.FirstOrDefault(h =>
                    h.EventType == MovieHistoryEventType.DownloadFolderImported &&
                    h.DownloadId == lastGrabbed.DownloadId);

                if (imported == null)
                {
                    return DownloadSpecDecision.Accept();
                }

                // This is really only a guard against redownloading the same release over
                // and over when the grabbed and imported qualities do not match, if they do
                // match skip this check.
                if (lastGrabbed.Quality.Equals(imported.Quality))
                {
                    return DownloadSpecDecision.Accept();
                }

                var release = subject.Release;

                if (release.DownloadProtocol == DownloadProtocol.Torrent)
                {
                    var torrentInfo = release as TorrentInfo;

                    if (torrentInfo?.InfoHash != null && torrentInfo.InfoHash.ToUpper() == lastGrabbed.DownloadId)
                    {
                        _logger.Debug("Has same torrent hash as a grabbed and imported release");
                        return DownloadSpecDecision.Reject(DownloadRejectionReason.AlreadyImportedSameHash, "Has same torrent hash as a grabbed and imported release");
                    }
                }

                // Only based on title because a release with the same title on another indexer/released at
                // a different time very likely has the exact same content and we don't need to also try it.
                if (release.Title.Equals(lastGrabbed.SourceTitle, StringComparison.InvariantCultureIgnoreCase))
                {
                    _logger.Debug("Has same release name as a grabbed and imported release");
                    return DownloadSpecDecision.Reject(DownloadRejectionReason.AlreadyImportedSameName, "Has same release name as a grabbed and imported release");
                }
            }

            return DownloadSpecDecision.Accept();
        }

        private DownloadSpecDecision CheckEdition(RemoteMovie subject)
        {
            var history = _historyService.GetByMovieId(subject.Movie.Id, null)
                .Where(h => h.MovieId == subject.Movie.Id).OrderByDescending(h => h.Date).ThenByDescending(h => h.Id).ToList();
            var grabs = history.Where(h => h.EventType == MovieHistoryEventType.Grabbed && !string.IsNullOrWhiteSpace(h.DownloadId)).ToList();

            var editions = _editions.GetByMovie(subject.Movie.Id);

            // A known release remains shared while that imported file is still current.
            // Historical imports must not prevent reacquiring a missing/replaced file.
            foreach (var grab in grabs.Where(g => history.Any(h => IsImportOf(h, g) && h.MovieEditionId.HasValue &&
                int.TryParse(h.Data.GetValueOrDefault("fileId"), out var fileId) && fileId > 0 &&
                editions.Any(e => e.Id == h.MovieEditionId && e.MovieFileId == fileId))))
            {
                if (IsKnownRelease(subject.Release, grab))
                {
                    return DownloadSpecDecision.Reject(subject.Release is TorrentInfo ? DownloadRejectionReason.AlreadyImportedSameHash : DownloadRejectionReason.AlreadyImportedSameName,
                        "This release was already imported for this movie; review its edition assignment");
                }
            }

            if (subject.CurrentFile == null)
            {
                return DownloadSpecDecision.Accept();
            }

            var lastGrab = grabs.FirstOrDefault(h => h.MovieEditionId == subject.EditionContext.Edition.Id);
            var imported = lastGrab == null ? null : history.FirstOrDefault(h => IsImportOf(h, lastGrab));
            if (imported == null || lastGrab.Quality.Equals(imported.Quality))
            {
                return DownloadSpecDecision.Accept();
            }

            if (string.Equals(subject.Release.Title, lastGrab.SourceTitle, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.AlreadyImportedSameName, "Has same release name as a grabbed and imported release for this edition");
            }

            return DownloadSpecDecision.Accept();
        }

        private static bool IsImportOf(MovieHistory imported, MovieHistory grab)
        {
            return imported.EventType == MovieHistoryEventType.DownloadFolderImported && imported.Date >= grab.Date &&
                string.Equals(imported.DownloadId, grab.DownloadId, StringComparison.OrdinalIgnoreCase) &&
                imported.MovieEditionId == grab.MovieEditionId &&
                imported.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID) == grab.Data.GetValueOrDefault(MovieHistory.DOWNLOAD_CLIENT_ID);
        }

        private static bool IsKnownRelease(ReleaseInfo release, MovieHistory grab)
        {
            if (!int.TryParse(grab.Data.GetValueOrDefault("protocol"), out var protocol) || protocol != (int)release.DownloadProtocol)
            {
                return false;
            }

            if (release is TorrentInfo torrent && !string.IsNullOrWhiteSpace(torrent.InfoHash))
            {
                return string.Equals(torrent.InfoHash, grab.Data.GetValueOrDefault("torrentInfoHash") ?? grab.DownloadId, StringComparison.OrdinalIgnoreCase);
            }

            var guid = grab.Data.GetValueOrDefault("guid");
            return !string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(release.Guid) && ReleaseIdentity.Matches(release, new ReleaseInfo
            {
                Guid = guid, Indexer = grab.Data.GetValueOrDefault("indexer"),
                IndexerId = int.TryParse(grab.Data.GetValueOrDefault("indexerId"), out var indexer) ? indexer : 0,
                DownloadProtocol = (DownloadProtocol)protocol
            });
        }
    }
}
