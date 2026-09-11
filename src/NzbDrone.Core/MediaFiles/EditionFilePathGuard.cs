using System;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEditionFilePathGuard
    {
        void Validate(Movie movie, string destination, int? replacingFileId, string sourcePath = null);
    }

    public class EditionFilePathGuard : IEditionFilePathGuard
    {
        private readonly IMediaFileService _files;
        private readonly IDiskProvider _disk;

        public EditionFilePathGuard(IMediaFileService files, IDiskProvider disk)
        {
            _files = files;
            _disk = disk;
        }

        public void Validate(Movie movie, string destination, int? replacingFileId, string sourcePath = null)
        {
            if (!movie.Path.IsParentPath(destination))
            {
                throw new InvalidOperationException("The destination must be inside the movie folder.");
            }

            var replacesDestination = false;
            foreach (var file in _files.GetFilesByMovie(movie.Id))
            {
                var storedPath = Path.Combine(movie.Path, file.RelativePath);
                // Shared basenames also collide for file-specific subtitles and metadata.
                if (!string.Equals(Path.ChangeExtension(storedPath, null), Path.ChangeExtension(destination, null), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.Id != replacingFileId)
                {
                    throw new InvalidOperationException("The destination belongs to another file. Use distinct edition filenames before importing or renaming.");
                }

                replacesDestination = storedPath.PathEquals(destination);
            }

            if (_disk.FileExists(destination) && !replacesDestination && (sourcePath == null || !sourcePath.PathEquals(destination)))
            {
                throw new InvalidOperationException("The destination already exists. Choose a distinct edition filename; existing files will not be overwritten.");
            }
        }
    }
}
