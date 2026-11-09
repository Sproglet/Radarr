using System;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionRecoveryService
    {
        MovieEditionContext Recover(Movie movie, MovieHistory grab, DownloadHistory downloadGrab);
        MovieEditionContext RecoverPending(Movie movie, PendingRelease pending);
        void ValidateEvidence(MovieEditionContext context, ParsedMovieInfo parsed);
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

        public void ValidateEvidence(MovieEditionContext context, ParsedMovieInfo parsed)
        {
            if (context == null || string.IsNullOrWhiteSpace(parsed?.SimpleReleaseTitle))
            {
                return; // An obfuscated/unlabelled filename does not revoke persisted intent.
            }

            var evidence = MovieEditionEvidence.FromParsed(parsed);
            if (!evidence.Valid)
            {
                throw new InvalidOperationException("Invalid edition evidence; manual review required.");
            }

            var attributes = MovieEditionClassifier.ParseEvidence(evidence.Label, evidence.Presentation, false);
            if (attributes.Status != EditionClassificationStatus.Matched)
            {
                throw new InvalidOperationException("Conflicting or unrecognized edition evidence; manual review required.");
            }

            foreach (var attribute in attributes.Attributes)
            {
                if (context.Edition.Attributes.TryGetValue(attribute.Key, out var assigned) && assigned != attribute.Value)
                {
                    throw new InvalidOperationException("Release edition evidence contradicts the persisted assignment; manual review required.");
                }
            }
        }

        public MovieEditionContext RecoverPending(Movie movie, PendingRelease pending)
        {
            if (pending.MovieId != movie.Id)
            {
                throw new InvalidOperationException("Pending assignment belongs to another movie.");
            }

            return ResolveAssigned(movie, pending.MovieEditionId);
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
            return ResolveAssigned(movie, editionId);
        }

        private MovieEditionContext ResolveAssigned(Movie movie, int? editionId)
        {
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
