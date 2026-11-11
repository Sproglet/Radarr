using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionManagementService
    {
        MovieEdition Create(MovieEdition proposal);
        MovieEdition SelectPrimary(int movieId, int editionId, int expectedPrimaryId, int? expectedFileId, int expectedProfileId);
        MovieEdition SetMonitoring(MovieEditionMonitoringChange change);
    }

    public class MovieEditionManagementService : IMovieEditionManagementService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionNamingPreviewService _preview;
        private readonly IEditionFilePathGuard _paths;
        private readonly IDiskProvider _disk;
        private readonly IEventAggregator _events;

        public MovieEditionManagementService(IMovieRepository movies, IMovieEditionRepository editions,
            IMovieEditionNamingPreviewService preview, IEditionFilePathGuard paths, IDiskProvider disk, IEventAggregator events)
        {
            _movies = movies;
            _editions = editions;
            _preview = preview;
            _paths = paths;
            _disk = disk;
            _events = events;
        }

        public MovieEdition SelectPrimary(int movieId, int editionId, int expectedPrimaryId, int? expectedFileId, int expectedProfileId)
        {
            Movie movie;
            MovieEdition target;
            using (MovieEditionAcquisitionGate.Acquire(movieId))
            using (MovieFileOperationLock.Acquire(movieId))
            {
                _ = _movies.Find(movieId) ?? throw new ModelNotFoundException(typeof(Movie), movieId);
                target = _editions.Find(editionId) ?? throw new ModelNotFoundException(typeof(MovieEdition), editionId);
                if (target.MovieId != movieId)
                {
                    throw new InvalidOperationException("The selected edition does not belong to this movie.");
                }

                _editions.SelectPrimary(movieId, editionId, expectedPrimaryId, expectedFileId, expectedProfileId);
                movie = _movies.Get(movieId);
                target = _editions.Get(editionId);
            }

            // Publish after releasing locks: synchronous consumers can query or call the API.
            _events.PublishEvent(new MovieUpdatedEvent(movie));
            return target;
        }

        public MovieEdition Create(MovieEdition proposal)
        {
            if (proposal == null || proposal.MovieId <= 0)
            {
                throw new ArgumentException("A positive movie ID is required.");
            }

            using var acquisition = MovieEditionAcquisitionGate.Acquire(proposal.MovieId);
            using var lease = MovieFileOperationLock.Acquire(proposal.MovieId);
            var movie = _movies.Find(proposal.MovieId) ?? throw new ModelNotFoundException(typeof(Movie), proposal.MovieId);
            var expected = _editions.GetByMovie(movie.Id);
            var preview = _preview.Preview(proposal);
            if (!preview.NamingValid)
            {
                throw new InvalidOperationException(string.Join(" ", preview.Rejections));
            }

            var destination = Path.Combine(movie.Path, preview.Samples.Single(s => !s.MovieEditionId.HasValue).RelativePath);
            _paths.Validate(movie, destination, null);
            var directory = Path.GetDirectoryName(destination);
            if (_disk.FolderExists(directory))
            {
                var basename = Path.ChangeExtension(destination, null);
                if (_disk.GetFiles(directory, false).Any(path => !MediaFileExtensions.Extensions.Contains(Path.GetExtension(path)) &&
                    (string.Equals(Path.ChangeExtension(path, null), basename, StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith(basename + ".", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException("The proposed basename has existing sidecar files. Resolve their ownership before adding an edition.");
                }
            }

            var edition = preview.ProposedEdition;
            edition.EditionKey = "custom-" + Guid.NewGuid().ToString("N");
            return _editions.CreateUnmonitored(edition, movie, expected);
        }

        public MovieEdition SetMonitoring(MovieEditionMonitoringChange change)
        {
            if (change == null || change.MovieId <= 0 || change.EditionId <= 0 || change.ExpectedName == null)
            {
                throw new ArgumentException("Provide a movie, edition and complete expected monitoring state.");
            }

            Movie movie;
            MovieEdition target;
            using (MovieEditionAcquisitionGate.Acquire(change.MovieId))
            using (MovieFileOperationLock.Acquire(change.MovieId))
            {
                _ = _movies.Find(change.MovieId) ?? throw new ModelNotFoundException(typeof(Movie), change.MovieId);
                target = _editions.Find(change.EditionId) ?? throw new ModelNotFoundException(typeof(MovieEdition), change.EditionId);
                if (target.MovieId != change.MovieId)
                {
                    throw new InvalidOperationException("The selected edition does not belong to this movie.");
                }

                target = _editions.SetMonitoring(change);
                movie = _movies.Get(change.MovieId);
            }

            _events.PublishEvent(new MovieUpdatedEvent(movie));
            return target;
        }
    }
}
