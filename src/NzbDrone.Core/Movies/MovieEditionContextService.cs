using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieEditionContextService
    {
        MovieEditionContext Resolve(Movie movie, int? editionId, string sourcePath = null);
        void Validate(Movie movie, MovieEditionContext context);
    }

    public class MovieEditionContextService : IMovieEditionContextService
    {
        private readonly IMovieEditionRepository _editions;
        private readonly IMediaFileRepository _files;
        private readonly IQualityProfileRepository _profiles;

        public MovieEditionContextService(IMovieEditionRepository editions, IMediaFileRepository files, IQualityProfileRepository profiles)
        {
            _editions = editions;
            _files = files;
            _profiles = profiles;
        }

        public MovieEditionContext Resolve(Movie movie, int? editionId, string sourcePath = null)
        {
            var editions = _editions.GetByMovie(movie.Id);
            if (sourcePath != null)
            {
                var owners = _files.GetFilesByMovie(movie.Id)
                    .Where(f => Path.Combine(movie.Path, f.RelativePath).PathEquals(sourcePath)).ToList();
                if (owners.Count > 1)
                {
                    throw new InvalidOperationException("Multiple file records match this path. Resolve the conflict before importing.");
                }

                var ownerEditionId = owners.SingleOrDefault()?.MovieEditionId;
                if (ownerEditionId.HasValue && editionId.HasValue && ownerEditionId != editionId)
                {
                    throw new InvalidOperationException("The source file already belongs to another edition. It cannot be reassigned by importing it.");
                }

                editionId ??= ownerEditionId;
            }

            if (!editionId.HasValue && editions.Count != 1)
            {
                throw new InvalidOperationException("Select an edition explicitly; this movie has multiple or missing edition targets.");
            }

            var edition = editionId.HasValue ? editions.SingleOrDefault(e => e.Id == editionId) : editions.Single();
            if (edition == null)
            {
                throw new InvalidOperationException("The selected edition does not belong to this movie.");
            }

            var file = edition.MovieFileId.HasValue ? _files.Find(edition.MovieFileId.Value) : null;
            if (edition.MovieFileId.HasValue && (file == null || file.MovieId != movie.Id || file.MovieEditionId != edition.Id))
            {
                throw new InvalidOperationException("The edition has invalid file ownership. Repair its links before importing.");
            }

            return new MovieEditionContext(edition, _profiles.Get(edition.QualityProfileId), file, editions.Count > 1);
        }

        public void Validate(Movie movie, MovieEditionContext context)
        {
            if (context.Edition.MovieId != movie.Id)
            {
                throw new InvalidOperationException("The edition context belongs to another movie.");
            }

            var current = Resolve(movie, context.Edition.Id);
            if (current.Edition.MovieFileId != context.Edition.MovieFileId || current.Edition.QualityProfileId != context.Edition.QualityProfileId ||
                current.CurrentFile?.Id != context.CurrentFile?.Id || current.CurrentFile?.RelativePath != context.CurrentFile?.RelativePath ||
                context.CurrentFile?.MovieEditionId != current.CurrentFile?.MovieEditionId || context.QualityProfile?.Id != current.QualityProfile.Id || context.HasMultipleEditions != current.HasMultipleEditions)
            {
                throw new InvalidOperationException("The edition changed. Reload the import before replacing any files.");
            }
        }
    }
}
