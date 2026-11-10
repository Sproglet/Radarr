using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionManagementService
    {
        MovieEdition Create(MovieEdition proposal);
    }

    public class MovieEditionManagementService : IMovieEditionManagementService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionNamingPreviewService _preview;
        private readonly IEditionFilePathGuard _paths;
        private readonly IDiskProvider _disk;

        public MovieEditionManagementService(IMovieRepository movies, IMovieEditionRepository editions,
            IMovieEditionNamingPreviewService preview, IEditionFilePathGuard paths, IDiskProvider disk)
        {
            _movies = movies;
            _editions = editions;
            _preview = preview;
            _paths = paths;
            _disk = disk;
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
    }
}
