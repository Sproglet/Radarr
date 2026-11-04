using System;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionRecoveryService
    {
        MovieEditionContext Recover(Movie movie, MovieHistory grab, DownloadHistory downloadGrab);
    }

    // Recovery primitive only. Never reclassifies persisted intent using current matching rules.
    // Callers still need to review contradictory evidence and pass the acquisition/import gates.
    public class MovieEditionRecoveryService : IMovieEditionRecoveryService
    {
        private readonly IMovieEditionContextService _contexts;
        private readonly IMovieEditionRepository _editions;

        public MovieEditionRecoveryService(IMovieEditionContextService contexts, IMovieEditionRepository editions)
        {
            _contexts = contexts;
            _editions = editions;
        }

        public MovieEditionContext Recover(Movie movie, MovieHistory grab, DownloadHistory downloadGrab)
        {
            if ((grab != null && (grab.MovieId != movie.Id || grab.EventType != MovieHistoryEventType.Grabbed)) ||
                (downloadGrab != null && (downloadGrab.MovieId != movie.Id || downloadGrab.EventType != DownloadHistoryEventType.DownloadGrabbed)))
            {
                throw new InvalidOperationException("Recovery requires grab records for the same movie.");
            }

            if (grab != null && downloadGrab != null && !string.Equals(grab.DownloadId, downloadGrab.DownloadId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Recovery grab records belong to different downloads.");
            }

            if (grab?.MovieEditionId != null && downloadGrab?.MovieEditionId != null && grab.MovieEditionId != downloadGrab.MovieEditionId)
            {
                throw new InvalidOperationException("Recovery grab records disagree about the edition. Review the download manually.");
            }

            var editionId = grab?.MovieEditionId ?? downloadGrab?.MovieEditionId;
            if (editionId.HasValue)
            {
                return _contexts.Resolve(movie, editionId);
            }

            if (_editions.GetByMovie(movie.Id).Count != 1)
            {
                throw new InvalidOperationException("Unassigned legacy grabs require manual edition review for this movie.");
            }

            return null;
        }
    }
}
