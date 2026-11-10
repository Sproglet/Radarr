using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport.Manual
{
    public interface IManualImportService
    {
        List<ManualImportItem> GetMediaFiles(int movieId);
        List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? movieId, bool filterExistingFiles);
        List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? movieId, bool filterExistingFiles, int? downloadClientId);
        ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags);
        ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags, int? movieEditionId);
        ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags, int? movieEditionId, int? downloadClientId);
    }

    public class ManualImportService : IExecute<ManualImportCommand>, IManualImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IParsingService _parsingService;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IMovieService _movieService;
        private readonly IImportApprovedMovie _importApprovedMovie;
        private readonly IAggregationService _aggregationService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IDownloadedMovieImportService _downloadedMovieImportService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ILocalMovieCustomFormatCalculationService _localMovieFormatCalculator;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMovieEditionContextService _editionContexts;
        private readonly Logger _logger;

        public ManualImportService(IDiskProvider diskProvider,
                                   IParsingService parsingService,
                                   IDiskScanService diskScanService,
                                   IMakeImportDecision importDecisionMaker,
                                   IMovieService movieService,
                                   IAggregationService aggregationService,
                                   IImportApprovedMovie importApprovedMovie,
                                   ITrackedDownloadService trackedDownloadService,
                                   IDownloadedMovieImportService downloadedMovieImportService,
                                   IMediaFileService mediaFileService,
                                   ILocalMovieCustomFormatCalculationService localMovieFormatCalculator,
                                   ICustomFormatCalculationService formatCalculator,
                                   IEventAggregator eventAggregator,
                                   IMovieEditionContextService editionContexts,
                                   Logger logger)
        {
            _diskProvider = diskProvider;
            _parsingService = parsingService;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _movieService = movieService;
            _aggregationService = aggregationService;
            _importApprovedMovie = importApprovedMovie;
            _trackedDownloadService = trackedDownloadService;
            _downloadedMovieImportService = downloadedMovieImportService;
            _mediaFileService = mediaFileService;
            _localMovieFormatCalculator = localMovieFormatCalculator;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _editionContexts = editionContexts;
            _logger = logger;
        }

        public List<ManualImportItem> GetMediaFiles(int movieId)
        {
            var movie = _movieService.GetMovie(movieId);
            var directoryInfo = new DirectoryInfo(movie.Path);
            var movieFiles = _mediaFileService.GetFilesByMovie(movieId);

            var items = movieFiles.Select(movieFile => MapItem(movieFile, movie, directoryInfo.Name)).ToList();

            var mediaFiles = _diskScanService.FilterPaths(movie.Path, _diskScanService.GetVideoFiles(movie.Path)).ToList();
            var unmappedFiles = MediaFileService.FilterExistingFiles(mediaFiles, movieFiles, movie);

            items.AddRange(unmappedFiles.Select(file =>
                new ManualImportItem
                {
                    Path = Path.Combine(movie.Path, file),
                    FolderName = directoryInfo.Name,
                    RelativePath = movie.Path.GetRelativePath(file),
                    Name = Path.GetFileNameWithoutExtension(file),
                    Movie = movie,
                    ReleaseGroup = string.Empty,
                    Quality = new QualityModel(Quality.Unknown),
                    Languages = new List<Language> { Language.Unknown },
                    Size = _diskProvider.GetFileSize(file),
                    Rejections = Enumerable.Empty<ImportRejection>()
                }));

            return items;
        }

        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? movieId, bool filterExistingFiles)
        {
            return GetMediaFiles(path, downloadId, movieId, filterExistingFiles, null);
        }

        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, int? movieId, bool filterExistingFiles, int? downloadClientId)
        {
            if (downloadClientId.HasValue && string.IsNullOrWhiteSpace(downloadId))
            {
                throw new InvalidOperationException("A download ID is required with a download client.");
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = GetTrackedDownload(downloadId, downloadClientId);

                if (trackedDownload == null)
                {
                    return new List<ManualImportItem>();
                }

                path = trackedDownload.ImportItem?.OutputPath.FullPath ?? trackedDownload.DownloadItem.OutputPath.FullPath;
            }

            if (!_diskProvider.FolderExists(path))
            {
                if (!_diskProvider.FileExists(path))
                {
                    return new List<ManualImportItem>();
                }

                var rootFolder = Path.GetDirectoryName(path);
                return WithClient(new List<ManualImportItem> { ProcessFile(rootFolder, rootFolder, path, downloadId, null, downloadClientId) }, downloadClientId);
            }

            return WithClient(ProcessFolder(path, path, downloadId, movieId, filterExistingFiles, downloadClientId), downloadClientId);
        }

        public ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags)
        {
            return ReprocessItem(path, downloadId, movieId, releaseGroup, quality, languages, indexerFlags, null);
        }

        public ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags, int? movieEditionId)
        {
            return ReprocessItem(path, downloadId, movieId, releaseGroup, quality, languages, indexerFlags, movieEditionId, null);
        }

        public ManualImportItem ReprocessItem(string path, string downloadId, int movieId, string releaseGroup, QualityModel quality, List<Language> languages, int indexerFlags, int? movieEditionId, int? downloadClientId)
        {
            var rootFolder = Path.GetDirectoryName(path);
            var movie = _movieService.GetMovie(movieId);

            var languageParse = LanguageParser.ParseLanguages(path);

            if (languageParse.Count <= 1 && languageParse.First() == Language.Unknown && movie != null)
            {
                languageParse = new List<Language> { movie.MovieMetadata.Value.OriginalLanguage };
                _logger.Debug("Language couldn't be parsed from release, fallback to movie original language: {0}", movie.MovieMetadata.Value.OriginalLanguage.Name);
            }

            var tracked = GetTrackedDownload(downloadId, downloadClientId);
            var downloadClientItem = tracked?.DownloadItem;
            var finalReleaseGroup = releaseGroup.IsNullOrWhiteSpace()
                ? ReleaseGroupParser.ParseReleaseGroup(path)
                : releaseGroup;
            var finalQuality = (quality?.Quality ?? Quality.Unknown) == Quality.Unknown ? QualityParser.ParseQuality(path) : quality;
            var finalLanguages =
                languages?.Count <= 1 && (languages?.SingleOrDefault() ?? Language.Unknown) == Language.Unknown
                    ? languageParse
                    : languages;

            var localMovie = new LocalMovie();
            localMovie.Movie = movie;
            localMovie.FileMovieInfo = Parser.Parser.ParseMoviePath(path);
            localMovie.DownloadClientMovieInfo = downloadClientItem == null ? null : Parser.Parser.ParseMovieTitle(downloadClientItem.Title);
            localMovie.DownloadItem = downloadClientItem;
            localMovie.Path = path;
            localMovie.SceneSource = SceneSource(movie, rootFolder);
            localMovie.ExistingFile = movie.Path.IsParentPath(path);
            localMovie.Size = _diskProvider.GetFileSize(path);
            localMovie.ReleaseGroup = finalReleaseGroup;
            localMovie.Languages = finalLanguages;
            localMovie.Quality = finalQuality;
            localMovie.IndexerFlags = (IndexerFlags)indexerFlags;

            try
            {
                localMovie.EditionContext = _editionContexts.Resolve(movie, movieEditionId, path);
                ValidateTrackedAssignment(localMovie, tracked);
            }
            catch (InvalidOperationException ex)
            {
                return MapItem(new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.InvalidMovie, ex.Message)), rootFolder, downloadId, null);
            }

            _localMovieFormatCalculator.UpdateMovieCustomFormats(localMovie);

            // Augment movie file so imported files have all additional information an automatic import would
            localMovie = _aggregationService.Augment(localMovie, downloadClientItem);

            // Reapply the user-chosen values.
            localMovie.Movie = movie;
            localMovie.ReleaseGroup = finalReleaseGroup;
            localMovie.Quality = finalQuality;
            localMovie.Languages = finalLanguages;
            localMovie.IndexerFlags = (IndexerFlags)indexerFlags;

            return MapItem(_importDecisionMaker.GetDecision(localMovie, downloadClientItem), rootFolder, downloadId, null);
        }

        private List<ManualImportItem> ProcessFolder(string rootFolder, string baseFolder, string downloadId, int? movieId, bool filterExistingFiles, int? downloadClientId = null)
        {
            DownloadClientItem downloadClientItem = null;
            Movie movie = null;

            var directoryInfo = new DirectoryInfo(baseFolder);

            if (movieId.HasValue)
            {
                movie = _movieService.GetMovie(movieId.Value);
            }
            else
            {
                try
                {
                    movie = _parsingService.GetMovie(directoryInfo.Name);
                }
                catch (MultipleMoviesFoundException e)
                {
                    _logger.Warn(e, "Unable to match movie by title");
                }
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = GetTrackedDownload(downloadId, downloadClientId);
                downloadClientItem = trackedDownload.DownloadItem;

                if (movie == null)
                {
                    movie = trackedDownload.RemoteMovie?.Movie;
                }
            }

            if (movie == null)
            {
                // Filter paths based on the rootFolder, so files in subfolders that should be ignored are ignored.
                // It will lead to some extra directories being checked for files, but it saves the processing of them and is cleaner than
                // teaching FilterPaths to know whether it's processing a file or a folder and changing it's filtering based on that.
                // If the movie is unknown for the directory and there are more than 100 files in the folder don't process the items before returning.
                var files = _diskScanService.FilterPaths(rootFolder, _diskScanService.GetVideoFiles(baseFolder, false));

                if (files.Count > 100)
                {
                    _logger.Warn("Unable to determine movie from folder name and found more than 100 files. Skipping parsing");

                    return ProcessDownloadDirectory(rootFolder, files);
                }

                var subfolders = _diskScanService.FilterPaths(rootFolder, _diskProvider.GetDirectories(baseFolder));

                var processedFiles = files.Select(file => ProcessFile(rootFolder, baseFolder, file, downloadId, null, downloadClientId));
                var processedFolders = subfolders.SelectMany(subfolder => ProcessFolder(rootFolder, subfolder, downloadId, null, filterExistingFiles, downloadClientId));

                return processedFiles.Concat(processedFolders).Where(i => i != null).ToList();
            }

            var folderInfo = Parser.Parser.ParseMovieTitle(directoryInfo.Name);
            var movieFiles = _diskScanService.FilterPaths(rootFolder, _diskScanService.GetVideoFiles(baseFolder).ToList());
            var decisions = _importDecisionMaker.GetImportDecisions(movieFiles, movie, downloadClientItem, folderInfo, SceneSource(movie, baseFolder), filterExistingFiles);

            return decisions.Select(decision => MapItem(decision, rootFolder, downloadId, directoryInfo.Name)).ToList();
        }

        private ManualImportItem ProcessFile(string rootFolder, string baseFolder, string file, string downloadId, Movie movie = null, int? downloadClientId = null)
        {
            try
            {
                var trackedDownload = GetTrackedDownload(downloadId, downloadClientId);
                var relativeFile = baseFolder.GetRelativePath(file);

                if (movie == null)
                {
                    movie = _parsingService.GetMovie(relativeFile.Split('\\', '/')[0]);
                }

                if (movie == null)
                {
                    movie = _parsingService.GetMovie(relativeFile);
                }

                if (trackedDownload != null && movie == null)
                {
                    movie = trackedDownload?.RemoteMovie?.Movie;
                }

                if (movie == null)
                {
                    var relativeParseInfo = Parser.Parser.ParseMoviePath(relativeFile);

                    if (relativeParseInfo != null)
                    {
                        movie = _movieService.FindByTitle(relativeParseInfo.PrimaryMovieTitle, relativeParseInfo.Year);
                    }
                }

                if (movie == null)
                {
                    var localMovie = new LocalMovie();
                    localMovie.Path = file;
                    localMovie.ReleaseGroup = ReleaseGroupParser.ParseReleaseGroup(file);
                    localMovie.Quality = QualityParser.ParseQuality(file);
                    localMovie.Languages = LanguageParser.ParseLanguages(file);
                    localMovie.Size = _diskProvider.GetFileSize(file);

                    return MapItem(new ImportDecision(localMovie,
                        new ImportRejection(ImportRejectionReason.UnknownMovie, "Unknown Movie")),
                        rootFolder,
                        downloadId,
                        null);
                }

                var importDecisions = _importDecisionMaker.GetImportDecisions(new List<string> { file }, movie, trackedDownload?.DownloadItem, null, SceneSource(movie, baseFolder));

                if (importDecisions.Any())
                {
                    return MapItem(importDecisions.First(), rootFolder, downloadId, null);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to process file: {0}", file);
            }

            return new ManualImportItem
            {
                DownloadId = downloadId,
                Path = file,
                RelativePath = rootFolder.GetRelativePath(file),
                Name = Path.GetFileNameWithoutExtension(file),
                Size = _diskProvider.GetFileSize(file),
                Rejections = new List<ImportRejection>()
            };
        }

        private List<ManualImportItem> ProcessDownloadDirectory(string rootFolder, List<string> videoFiles)
        {
            var items = new List<ManualImportItem>();

            foreach (var file in videoFiles)
            {
                var localMovie = new LocalMovie();
                localMovie.Path = file;
                localMovie.Quality = new QualityModel(Quality.Unknown);
                localMovie.Languages = new List<Language> { Language.Unknown };
                localMovie.ReleaseGroup = ReleaseGroupParser.ParseReleaseGroup(file);
                localMovie.Size = _diskProvider.GetFileSize(file);

                items.Add(MapItem(new ImportDecision(localMovie), rootFolder, null, null));
            }

            return items;
        }

        private bool SceneSource(Movie movie, string folder)
        {
            return !(movie.Path.PathEquals(folder) || movie.Path.IsParentPath(folder));
        }

        private static List<ManualImportItem> WithClient(List<ManualImportItem> items, int? clientId)
        {
            foreach (var item in items.Where(i => i != null))
            {
                item.DownloadClientId = clientId ?? item.DownloadClientId;
            }

            return items;
        }

        private static void ValidateTrackedAssignment(LocalMovie local, TrackedDownload tracked)
        {
            if (tracked == null)
            {
                return;
            }

            if (tracked.EditionRecoveryError != null || tracked.RemoteMovie?.EditionRecoveryError != null ||
                tracked.RemoteMovie?.Movie?.Id != local.Movie.Id ||
                (tracked.RemoteMovie.EditionContext != null && tracked.RemoteMovie.EditionContext.Edition.Id != local.EditionContext?.Edition.Id))
            {
                throw new InvalidOperationException("The selected movie/edition conflicts with this download's saved assignment; resolve it before importing.");
            }
        }

        private TrackedDownload GetTrackedDownload(string downloadId, int? downloadClientId = null)
        {
            if (downloadClientId.HasValue && (downloadClientId <= 0 || string.IsNullOrWhiteSpace(downloadId)))
            {
                throw new InvalidOperationException("A positive client ID and a download ID are required.");
            }

            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return null;
            }

            var tracked = downloadClientId.HasValue ? _trackedDownloadService.Find(downloadClientId.Value, downloadId) : _trackedDownloadService.Find(downloadId);
            if (tracked == null && downloadClientId.HasValue)
            {
                throw new InvalidOperationException("The download is not tracked for the selected client.");
            }

            return tracked;
        }

        private ManualImportItem MapItem(ImportDecision decision, string rootFolder, string downloadId, string folderName)
        {
            var item = new ManualImportItem();

            item.Path = decision.LocalMovie.Path;
            item.FolderName = folderName;
            item.RelativePath = rootFolder.GetRelativePath(decision.LocalMovie.Path);
            item.Name = Path.GetFileNameWithoutExtension(decision.LocalMovie.Path);
            item.DownloadId = downloadId;
            item.DownloadClientId = decision.LocalMovie.DownloadItem?.DownloadClientInfo?.Id;

            item.Quality = decision.LocalMovie.Quality;
            item.Size = _diskProvider.GetFileSize(decision.LocalMovie.Path);
            item.Languages = decision.LocalMovie.Languages;
            item.ReleaseGroup = decision.LocalMovie.ReleaseGroup;
            item.Rejections = decision.Rejections;
            item.IndexerFlags = (int)decision.LocalMovie.IndexerFlags;

            if (decision.LocalMovie.Movie != null)
            {
                item.Movie = decision.LocalMovie.Movie;

                item.CustomFormats = _localMovieFormatCalculator.ParseMovieCustomFormats(decision.LocalMovie);
                item.EditionAssignmentValid = decision.LocalMovie.EditionContext != null && !decision.Rejections.Any(r => r.Reason == ImportRejectionReason.InvalidMovie);
                item.CustomFormatScore = item.EditionAssignmentValid ? decision.LocalMovie.EffectiveQualityProfile?.CalculateCustomFormatScore(item.CustomFormats) ?? 0 : 0;
                item.MovieEditionId = decision.LocalMovie.EditionContext?.Edition.Id;
                item.MovieEditionName = decision.LocalMovie.EditionContext?.Edition.Name;
            }

            return item;
        }

        private ManualImportItem MapItem(MovieFile movieFile, Movie movie, string folderName)
        {
            var item = new ManualImportItem();

            item.Path = Path.Combine(movie.Path, movieFile.RelativePath);
            item.FolderName = folderName;
            item.RelativePath = movieFile.RelativePath;
            item.Name = Path.GetFileNameWithoutExtension(movieFile.Path);
            item.Movie = movie;
            item.ReleaseGroup = movieFile.ReleaseGroup;
            item.Quality = movieFile.Quality;
            item.Languages = movieFile.Languages;
            item.IndexerFlags = (int)movieFile.IndexerFlags;
            item.Size = _diskProvider.GetFileSize(item.Path);
            item.Rejections = Enumerable.Empty<ImportRejection>();
            item.MovieFileId = movieFile.Id;
            item.MovieEditionId = movieFile.MovieEditionId;
            item.CustomFormats = _formatCalculator.ParseCustomFormat(movieFile, movie);
            try
            {
                var context = _editionContexts.Resolve(movie, movieFile.MovieEditionId, item.Path);
                item.MovieEditionName = context?.Edition.Name;
                item.EditionAssignmentValid = context != null;
                item.CustomFormatScore = (context?.QualityProfile ?? movie.QualityProfile)?.CalculateCustomFormatScore(item.CustomFormats) ?? 0;
            }
            catch (InvalidOperationException ex)
            {
                item.Rejections = new[] { new ImportRejection(ImportRejectionReason.InvalidMovie, ex.Message) };
            }

            return item;
        }

        public void Execute(ManualImportCommand message)
        {
            _logger.ProgressTrace("Manually importing {0} files using mode {1}", message.Files.Count, message.ImportMode);

            // Validate all supplied download assignments before the first filesystem mutation.
            foreach (var file in message.Files.Where(f => f.DownloadId.IsNotNullOrWhiteSpace() || f.DownloadClientId.HasValue))
            {
                var tracked = GetTrackedDownload(file.DownloadId, file.DownloadClientId);
                var movie = _movieService.GetMovie(file.MovieId);
                ValidateTrackedAssignment(new LocalMovie { Movie = movie, EditionContext = _editionContexts.Resolve(movie, file.MovieEditionId, file.Path) }, tracked);
            }

            var imported = new List<ImportResult>();
            var importedTrackedDownload = new List<ManuallyImportedFile>();

            for (var i = 0; i < message.Files.Count; i++)
            {
                _logger.ProgressTrace("Processing file {0} of {1}", i + 1, message.Files.Count);

                var file = message.Files[i];
                var movie = _movieService.GetMovie(file.MovieId);
                var fileMovieInfo = Parser.Parser.ParseMoviePath(file.Path) ?? new ParsedMovieInfo();
                var existingFile = movie.Path.IsParentPath(file.Path);
                TrackedDownload trackedDownload = null;

                var localMovie = new LocalMovie
                {
                    ExistingFile = existingFile,
                    FileMovieInfo = fileMovieInfo,
                    Path = file.Path,
                    ReleaseGroup = file.ReleaseGroup,
                    Quality = file.Quality,
                    Languages = file.Languages,
                    IndexerFlags = (IndexerFlags)file.IndexerFlags,
                    Movie = movie,
                    EditionContext = _editionContexts.Resolve(movie, file.MovieEditionId, file.Path),
                    Size = 0
                };

                if (imported.Any(r => r.Result == ImportResultType.Imported && r.ImportDecision.LocalMovie.Movie.Id == movie.Id &&
                    r.ImportDecision.LocalMovie.EditionContext?.Edition.Id == localMovie.EditionContext?.Edition.Id))
                {
                    imported.Add(new ImportResult(new ImportDecision(localMovie), "This edition has already been imported by this command."));
                    continue;
                }

                if (file.DownloadId.IsNotNullOrWhiteSpace())
                {
                    trackedDownload = GetTrackedDownload(file.DownloadId, file.DownloadClientId);
                    ValidateTrackedAssignment(localMovie, trackedDownload);
                    localMovie.DownloadClientMovieInfo = trackedDownload?.RemoteMovie?.ParsedMovieInfo;
                    localMovie.DownloadItem = trackedDownload?.DownloadItem;
                }

                if (file.FolderName.IsNotNullOrWhiteSpace())
                {
                    localMovie.FolderMovieInfo = Parser.Parser.ParseMovieTitle(file.FolderName);
                    localMovie.SceneSource = !existingFile;
                }

                // Augment movie file so imported files have all additional information an automatic import would
                localMovie = _aggregationService.Augment(localMovie, trackedDownload?.DownloadItem);

                // Apply the user-chosen values.
                localMovie.Movie = movie;
                localMovie.ReleaseGroup = file.ReleaseGroup;
                localMovie.Quality = file.Quality;
                localMovie.IndexerFlags = (IndexerFlags)file.IndexerFlags;

                if (file.Languages is { Count: > 0 } && file.Languages.All(l => l is not null && l.IsValid()))
                {
                    localMovie.Languages = file.Languages;
                }

                _localMovieFormatCalculator.UpdateMovieCustomFormats(localMovie);

                // TODO: Cleanup non-tracked downloads
                var importDecision = new ImportDecision(localMovie);

                if (trackedDownload == null)
                {
                    imported.AddRange(_importApprovedMovie.Import(new List<ImportDecision> { importDecision }, !existingFile, null, message.ImportMode));
                }
                else
                {
                    var importResult = _importApprovedMovie.Import(new List<ImportDecision> { importDecision }, true, trackedDownload.DownloadItem, message.ImportMode).First();

                    imported.Add(importResult);

                    importedTrackedDownload.Add(new ManuallyImportedFile
                    {
                        TrackedDownload = trackedDownload,
                        ImportResult = importResult
                    });
                }
            }

            _logger.ProgressTrace("Manually imported {0} files", imported.Count);

            foreach (var groupedTrackedDownload in importedTrackedDownload.GroupBy(i => new
            {
                ClientId = i.TrackedDownload.DownloadClient,
                i.TrackedDownload.DownloadItem.DownloadId
            }).ToList())
            {
                var trackedDownload = groupedTrackedDownload.First().TrackedDownload;

                var importMovie = groupedTrackedDownload.First().ImportResult.ImportDecision.LocalMovie.Movie;
                var outputPath = trackedDownload.ImportItem?.OutputPath.FullPath ?? trackedDownload.DownloadItem.OutputPath.FullPath;

                if (_diskProvider.FolderExists(outputPath))
                {
                    if (_downloadedMovieImportService.ShouldDeleteFolder(
                            new DirectoryInfo(outputPath),
                            importMovie) && trackedDownload.DownloadItem.CanMoveFiles)
                    {
                        _diskProvider.DeleteFolder(outputPath, true);
                    }
                }

                if (groupedTrackedDownload.Select(c => c.ImportResult).Any(c => c.Result == ImportResultType.Imported))
                {
                    trackedDownload.State = TrackedDownloadState.Imported;
                    _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, importMovie.Id));
                }
            }
        }
    }
}
