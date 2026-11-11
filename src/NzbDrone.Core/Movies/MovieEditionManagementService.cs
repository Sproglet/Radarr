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
        MovieEdition SetQualityProfile(MovieEditionQualityProfileChange change);
        MovieEditionNamingPreview PreviewDefinition(MovieEditionDefinitionChange change);
        MovieEdition SetDefinition(MovieEditionDefinitionChange change);
    }

    public class MovieEditionManagementService : IMovieEditionManagementService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionNamingPreviewService _preview;
        private readonly IEditionFilePathGuard _paths;
        private readonly IDiskProvider _disk;
        private readonly IEventAggregator _events;
        private readonly IMovieEditionContextService _contexts;

        public MovieEditionManagementService(IMovieRepository movies, IMovieEditionRepository editions,
            IMovieEditionNamingPreviewService preview, IEditionFilePathGuard paths, IDiskProvider disk, IEventAggregator events, IMovieEditionContextService contexts)
        {
            _movies = movies;
            _editions = editions;
            _preview = preview;
            _paths = paths;
            _disk = disk;
            _events = events;
            _contexts = contexts;
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

        public MovieEdition SetQualityProfile(MovieEditionQualityProfileChange change)
        {
            if (change == null || change.MovieId <= 0 || change.EditionId <= 0 || change.ExpectedName == null || change.QualityProfileId <= 0)
            {
                throw new ArgumentException("Provide a movie and edition, complete expected state and a positive quality profile ID.");
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

                target = _editions.SetQualityProfile(change);
                movie = _movies.Get(change.MovieId);
            }

            _events.PublishEvent(new MovieUpdatedEvent(movie));
            return target;
        }

        public MovieEditionNamingPreview PreviewDefinition(MovieEditionDefinitionChange change)
        {
            ValidateDefinitionRequest(change);
            using var acquisition = MovieEditionAcquisitionGate.Acquire(change.MovieId);
            using var lease = MovieFileOperationLock.Acquire(change.MovieId);
            var (movie, target) = DefinitionTarget(change);
            var preview = DefinitionPreview(change, movie, target);
            return preview;
        }

        public MovieEdition SetDefinition(MovieEditionDefinitionChange change)
        {
            ValidateDefinitionRequest(change);
            Movie movie;
            MovieEdition target;
            using (MovieEditionAcquisitionGate.Acquire(change.MovieId))
            using (MovieFileOperationLock.Acquire(change.MovieId))
            {
                (movie, target) = DefinitionTarget(change);
                var expected = _editions.GetByMovie(movie.Id);
                var preview = DefinitionPreview(change, movie, target);
                if (!preview.EditingEnabled)
                {
                    throw new InvalidOperationException(string.Join(" ", preview.Rejections));
                }

                target = _editions.SetDefinition(change, preview.ProposedEdition, movie, expected);
                movie = _movies.Get(movie.Id);
            }

            _events.PublishEvent(new MovieUpdatedEvent(movie));
            return target;
        }

        private static void ValidateDefinitionRequest(MovieEditionDefinitionChange change)
        {
            if (change == null || change.MovieId <= 0 || change.EditionId <= 0 || change.ExpectedName == null ||
                change.Attributes == null || change.MatchRules == null)
            {
                throw new ArgumentException("Provide a movie and edition, name, full attributes/rules and complete expected state.");
            }
        }

        private (Movie Movie, MovieEdition Target) DefinitionTarget(MovieEditionDefinitionChange change)
        {
            var movie = _movies.Find(change.MovieId) ?? throw new ModelNotFoundException(typeof(Movie), change.MovieId);
            var target = _editions.Find(change.EditionId) ?? throw new ModelNotFoundException(typeof(MovieEdition), change.EditionId);
            if (target.MovieId != movie.Id)
            {
                throw new ModelNotFoundException(typeof(MovieEdition), change.EditionId);
            }

            if (!change.Matches(movie, target))
            {
                throw new InvalidOperationException("The movie or edition settings changed. Reload before editing its definition.");
            }

            return (movie, target);
        }

        private MovieEditionNamingPreview DefinitionPreview(MovieEditionDefinitionChange change, Movie movie, MovieEdition target)
        {
            var preview = _preview.PreviewEdit(new MovieEdition
            {
                Id = target.Id, MovieId = movie.Id, Name = change.Name, QualityProfileId = target.QualityProfileId,
                Attributes = change.Attributes, MatchRules = change.MatchRules
            });
            if (!preview.NamingValid)
            {
                return preview;
            }

            var destination = Path.Combine(movie.Path, preview.Samples.Single(s => s.MovieEditionId == target.Id).RelativePath);
            try
            {
                _paths.Validate(movie, destination, target.MovieFileId);
                var currentFile = target.MovieFileId.HasValue ? DefinitionFilePath(movie, target) : null;
                var basename = Path.ChangeExtension(destination, null);
                var keepsBasename = currentFile != null && string.Equals(Path.ChangeExtension(currentFile, null), basename, StringComparison.OrdinalIgnoreCase);
                var directory = Path.GetDirectoryName(destination);
                if (!keepsBasename && _disk.FolderExists(directory) && _disk.GetFiles(directory, false).Any(path =>
                    !MediaFileExtensions.Extensions.Contains(Path.GetExtension(path)) &&
                    (string.Equals(Path.ChangeExtension(path, null), basename, StringComparison.OrdinalIgnoreCase) || path.StartsWith(basename + ".", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException("The proposed basename has existing sidecar files. Resolve their ownership before changing the edition name.");
                }
            }
            catch (InvalidOperationException ex)
            {
                preview.Rejections.Add(ex.Message);
            }

            return preview;
        }

        private string DefinitionFilePath(Movie movie, MovieEdition target)
        {
            // Resolve ownership before accepting any existing sidecars as this file's.
            var context = _contexts.Resolve(movie, target.Id);
            return context.CurrentFile == null ? null : Path.Combine(movie.Path, context.CurrentFile.RelativePath);
        }
    }
}
