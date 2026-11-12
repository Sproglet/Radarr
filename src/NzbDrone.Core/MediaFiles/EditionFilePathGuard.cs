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
            string replacingPath = null;
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

                replacingPath = storedPath;
                replacesDestination = storedPath.PathEquals(destination);
            }

            // Unassigned videos also reserve their basename: otherwise importing a different
            // extension can attach their subtitles to the new edition on the next scan.
            var directory = Path.GetDirectoryName(destination);
            if (_disk.FolderExists(directory))
            {
                foreach (var path in _disk.GetFiles(directory, false))
                {
                    if (!MediaFileExtensions.Extensions.Contains(Path.GetExtension(path)) ||
                        !string.Equals(Path.ChangeExtension(path, null), Path.ChangeExtension(destination, null), StringComparison.OrdinalIgnoreCase) ||
                        (replacingPath != null && path.PathEquals(replacingPath)) ||
                        (sourcePath != null && path.PathEquals(sourcePath)))
                    {
                        continue;
                    }

                    throw new InvalidOperationException("The destination basename is used by an existing video. Resolve the conflict before importing or renaming.");
                }
            }

            if (_disk.FileExists(destination) && !replacesDestination && (sourcePath == null || !sourcePath.PathEquals(destination)))
            {
                throw new InvalidOperationException("The destination already exists. Choose a distinct edition filename; existing files will not be overwritten.");
            }
        }
    }
}
