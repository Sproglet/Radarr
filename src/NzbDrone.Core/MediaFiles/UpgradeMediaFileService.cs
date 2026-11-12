using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveMovieFiles _movieFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IMovieEditionContextService _editionContexts;
        private readonly IBuildFileNames _fileNames;
        private readonly IEditionFilePathGuard _paths;
        private readonly IConfigService _config;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMoveMovieFiles movieFileMover,
                                       IDiskProvider diskProvider,
                                       IMovieEditionContextService editionContexts,
                                       IBuildFileNames fileNames,
                                       IEditionFilePathGuard paths,
                                       IConfigService config,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieFileMover = movieFileMover;
            _diskProvider = diskProvider;
            _editionContexts = editionContexts;
            _fileNames = fileNames;
            _paths = paths;
            _config = config;
            _logger = logger;
        }

        public MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false)
        {
            using var operation = MovieFileOperationLock.Acquire(localMovie.Movie.Id);
            _logger.Trace("Upgrading movie file.");

            var moveFileResult = new MovieFileMoveResult();

            var existingFile = localMovie.EditionContext != null ? localMovie.CurrentFile :
                localMovie.Movie.MovieFileId > 0 ? localMovie.Movie.MovieFile : null;

            if (localMovie.EditionContext != null)
            {
                _editionContexts.Validate(localMovie.Movie, localMovie.EditionContext);
                _editionContexts.Resolve(localMovie.Movie, localMovie.EditionContext.Edition.Id, localMovie.Path);
                if (localMovie.EditionContext.HasMultipleEditions && _config.UseScriptImport)
                {
                    throw new InvalidOperationException("Custom import scripts are disabled for multi-edition movies until edition-aware script recovery is supported.");
                }

                if (movieFile.MovieEditionId != localMovie.EditionContext.Edition.Id)
                {
                    throw new InvalidOperationException("The replacement file has no matching edition assignment.");
                }

                var name = _fileNames.BuildFileName(localMovie.Movie, movieFile, null, localMovie.CustomFormats);
                var destination = _fileNames.BuildFilePath(localMovie.Movie, name, Path.GetExtension(localMovie.Path));
                _paths.Validate(localMovie.Movie, destination, existingFile?.Id);
                if (existingFile != null && Path.Combine(localMovie.Movie.Path, existingFile.RelativePath).PathEquals(localMovie.Path))
                {
                    throw new InvalidOperationException("The import source is the current file. Use existing-file reprocessing instead of upgrading it.");
                }
            }

            var rootFolder = _diskProvider.GetParentFolder(localMovie.Movie.Path);

            // If there are existing movie files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFile != null && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            if (existingFile != null)
            {
                var movieFilePath = Path.Combine(localMovie.Movie.Path, existingFile.RelativePath);
                var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(movieFilePath));
                string recycleBinPath = null;

                if (_diskProvider.FileExists(movieFilePath))
                {
                    _logger.Debug("Removing existing movie file: {0}", existingFile);
                    recycleBinPath = _recycleBinProvider.DeleteFile(movieFilePath, subfolder);
                }
                else
                {
                    _logger.Warn("Existing movie file missing from disk: {0}", movieFilePath);
                }

                moveFileResult.OldFiles.Add(new DeletedMovieFile(existingFile, recycleBinPath));
                _mediaFileService.Delete(existingFile, DeleteMediaFileReason.Upgrade);
            }

            localMovie.OldFiles = moveFileResult.OldFiles;

            if (copyOnly)
            {
                moveFileResult.MovieFile = _movieFileMover.CopyMovieFile(movieFile, localMovie);
            }
            else
            {
                moveFileResult.MovieFile = _movieFileMover.MoveMovieFile(movieFile, localMovie);
            }

            return moveFileResult;
        }
    }
}
