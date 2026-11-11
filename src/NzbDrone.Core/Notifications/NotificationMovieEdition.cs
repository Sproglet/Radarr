using System.Collections.Generic;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Notifications
{
    // Event-time target metadata, not an authorization or a current-file context.
    // In particular, deletion notifications run after the file pointer is cleared.
    public class NotificationMovieEdition
    {
        public int Id { get; set; }
        public int MovieId { get; set; }
        public bool IsResolved { get; set; }
        public string Error { get; set; }
        public string Name { get; set; }
        public string EditionKey { get; set; }
        public int? QualityProfileId { get; set; }
        public int? RuleRevision { get; set; }
        public Dictionary<string, string> Attributes { get; set; }
    }

    public interface INotificationMovieEditionService
    {
        NotificationMovieEdition ForFile(MovieFile file);
        NotificationMovieEdition ForRelease(RemoteMovie movie);
    }

    public class NotificationMovieEditionService : INotificationMovieEditionService
    {
        private readonly IMovieEditionRepository _editions;

        public NotificationMovieEditionService(IMovieEditionRepository editions)
        {
            _editions = editions;
        }

        public NotificationMovieEdition ForFile(MovieFile file)
        {
            return file?.MovieEditionId is int id ? Snapshot(id, file.MovieId, _editions.Find(id)) : null;
        }

        public NotificationMovieEdition ForRelease(RemoteMovie movie)
        {
            var edition = movie?.EditionContext?.Edition;
            return edition == null ? null : Snapshot(edition.Id, movie.Movie?.Id ?? 0, edition);
        }

        private static NotificationMovieEdition Snapshot(int id, int movieId, MovieEdition edition)
        {
            if (edition == null || edition.Id != id || edition.MovieId != movieId || movieId <= 0)
            {
                return new NotificationMovieEdition
                {
                    Id = id,
                    MovieId = movieId,
                    IsResolved = false,
                    Error = "The assigned edition is missing or belongs to another movie."
                };
            }

            return new NotificationMovieEdition
            {
                Id = edition.Id,
                MovieId = movieId,
                IsResolved = true,
                Name = edition.Name,
                EditionKey = edition.EditionKey,
                QualityProfileId = edition.QualityProfileId,
                RuleRevision = edition.RuleRevision,
                Attributes = edition.Attributes == null ? null : new Dictionary<string, string>(edition.Attributes)
            };
        }
    }
}
