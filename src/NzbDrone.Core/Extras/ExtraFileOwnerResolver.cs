using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Extras
{
    public interface IExtraFileOwnerResolver
    {
        MovieFile Resolve(Movie movie, string path, string fileNameBeforeRename = null);
    }

    public class ExtraFileOwnerResolver : IExtraFileOwnerResolver
    {
        private readonly IMediaFileService _files;
        private readonly IMovieEditionRepository _editions;

        public ExtraFileOwnerResolver(IMediaFileService files, IMovieEditionRepository editions)
        {
            _files = files;
            _editions = editions;
        }

        public MovieFile Resolve(Movie movie, string path, string fileNameBeforeRename = null)
        {
            var files = _files.GetFilesByMovie(movie.Id);
            var matches = files.Where(f => Matches(Path.Combine(movie.Path, f.RelativePath), path)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                return null;
            }

            // Legacy single-file libraries can contain subtitles without a matching basename.
            // Never make this guess in a multi-edition library.
            return files.Count == 1 && _editions.GetByMovie(movie.Id).Count == 1 ? files[0] : null;
        }

        public static bool Matches(string videoPath, string extraPath)
        {
            if (!Path.GetDirectoryName(videoPath).PathEquals(Path.GetDirectoryName(extraPath)))
            {
                return false;
            }

            var stem = Path.GetFileNameWithoutExtension(videoPath);
            var extraName = Path.GetFileName(extraPath);
            return extraName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
        }
    }
}
