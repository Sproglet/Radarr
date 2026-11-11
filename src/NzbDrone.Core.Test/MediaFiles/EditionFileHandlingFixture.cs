using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    // Real repositories, import/upgrade/move/rename/scan services, real synthetic files.
    // External media probing and OS transfer adapters are replaced; no downloaded media is used.
    public class EditionFileHandlingFixture : DbTest
    {
        private Movie _movie;
        private MovieEdition _imax;
        private MovieEdition _directors;
        private NamingConfig _naming;
        private MovieRepository _movies;
        private MovieEditionRepository _editions;
        private IMovieEditionContextService _contexts;
        private ImportApprovedMovie _importer;
        private readonly List<string> _recycled = new List<string>();

        [SetUp]
        public void Setup()
        {
            _recycled.Clear();
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<LocalMovie>(), It.IsAny<string>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<MovieFile>(), It.IsAny<Movie>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<MovieFile>())).Returns(new List<CustomFormat>());
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            _movies = Mocker.Resolve<MovieRepository>();
            _editions = Mocker.Resolve<MovieEditionRepository>();
            Mocker.SetConstant<IMovieRepository>(_movies);
            Mocker.SetConstant<IMovieEditionRepository>(_editions);
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            Mocker.SetConstant<IMediaFileService>(Mocker.Resolve<MediaFileService>());
            _contexts = Mocker.Resolve<MovieEditionContextService>();
            Mocker.SetConstant(_contexts);

            var profile = Db.Insert(new QualityProfile
            {
                Name = "Synthetic Profile",
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Language = Language.English
            });
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew()
                .With(m => m.Title = "Synthetic Lunar Voyage")
                .With(m => m.Year = 2026).BuildNew());
            _movie = _movies.Insert(new Movie
            {
                MovieMetadataId = metadata.Id,
                MovieMetadata = metadata,
                QualityProfileId = profile.Id,
                QualityProfile = profile,
                Path = Path.Combine(TempFolder, "movies", "Synthetic Lunar Voyage")
            });
            Directory.CreateDirectory(_movie.Path);
            _imax = _editions.Get(_movie.PrimaryEditionId.Value);
            _imax.Name = "Theatrical IMAX";
            _imax.Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "framing", "imax" } };
            Db.Update(_imax);
            _directors = _editions.Insert(new MovieEdition
            {
                MovieId = _movie.Id,
                EditionKey = "directors-3d",
                Name = "Directors Cut 3D",
                QualityProfileId = profile.Id,
                Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }
            });

            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(It.IsAny<int>())).Returns<int>(_movies.Get);
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>())).Returns<IEnumerable<int>>(ids => _movies.Get(ids).ToList());
            Mocker.GetMock<IMovieService>().Setup(s => s.UpdateMovie(It.IsAny<Movie>())).Returns<Movie>(_movies.Update);
            var disk = Mocker.GetMock<IDiskProvider>();
            disk.Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            disk.Setup(d => d.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            disk.Setup(d => d.GetParentFolder(It.IsAny<string>())).Returns<string>(Path.GetDirectoryName);
            disk.Setup(d => d.GetFileSize(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).Length);
            disk.Setup(d => d.GetFiles(It.IsAny<string>(), It.IsAny<bool>())).Returns<string, bool>((p, recursive) => Directory.GetFiles(p, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            disk.Setup(d => d.CreateFolder(It.IsAny<string>())).Callback<string>(p => Directory.CreateDirectory(p));
            disk.Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback<string, string, bool>((a, b, overwrite) => File.Move(a, b, overwrite));
            Mocker.GetMock<IRootFolderService>().Setup(s => s.GetBestRootFolderPath(It.IsAny<string>(), null)).Returns(Path.Combine(TempFolder, "movies"));
            Mocker.GetMock<IDiskTransferService>().Setup(s => s.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    if (mode == TransferMode.Move)
                    {
                        File.Move(source, destination, overwrite);
                    }
                    else
                    {
                        File.Copy(source, destination, overwrite);
                    }

                    return mode;
                });
            Mocker.GetMock<IRecycleBinProvider>().Setup(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((path, _) =>
            {
                _recycled.Add(path);
                var saved = Path.Combine(TempFolder, "recycle", Guid.NewGuid().ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(saved));
                File.Move(path, saved);
                return saved;
            });
            Mocker.GetMock<IImportScript>().Setup(s => s.TryImport(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<TransferMode>())).Returns(ScriptImportDecision.DeferMove);
            _naming = new NamingConfig { RenameMovies = true, StandardMovieFormat = "{Movie Title} [{Managed Edition}]", ReplaceIllegalCharacters = true };
            Mocker.GetMock<INamingConfigService>().Setup(s => s.GetConfig()).Returns(_naming);
            Mocker.GetMock<IQualityDefinitionService>().Setup(s => s.Get(It.IsAny<Quality>())).Returns<Quality>(q => Quality.DefaultQualityDefinitions.First(d => d.Quality == q));
            Mocker.SetConstant<IBuildFileNames>(Mocker.Resolve<FileNameBuilder>());
            Mocker.SetConstant<ILocalMovieCustomFormatCalculationService>(Mocker.Resolve<LocalMovieCustomFormatCalculationService>());
            Mocker.SetConstant<IEditionFilePathGuard>(Mocker.Resolve<EditionFilePathGuard>());
            Mocker.SetConstant<IMoveMovieFiles>(Mocker.Resolve<MovieFileMovingService>());
            Mocker.SetConstant<IUpgradeMediaFiles>(Mocker.Resolve<UpgradeMediaFileService>());
            Mocker.SetConstant<IMediaFileTableCleanupService>(Mocker.Resolve<MediaFileTableCleanupService>());
            Mocker.GetMock<IMakeImportDecision>().Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), false)).Returns(new List<ImportDecision>());
            Mocker.GetMock<IAggregationService>().Setup(s => s.Augment(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>())).Returns<LocalMovie, DownloadClientItem>((local, _) => local);

            Mocker.SetConstant<IExtraFileRepository<SubtitleFile>>(Mocker.Resolve<ExtraFileRepository<SubtitleFile>>());
            var subtitleFiles = Mocker.Resolve<SubtitleFileService>();
            Mocker.SetConstant<ISubtitleFileService>(subtitleFiles);
            Mocker.SetConstant<IExtraFileService<SubtitleFile>>(subtitleFiles);
            var subtitles = Mocker.Resolve<SubtitleService>();
            var extras = new ExtraService(Mocker.Resolve<IMediaFileService>(), Mocker.Resolve<IMovieService>(), disk.Object,
                Mocker.GetMock<IConfigService>().Object, new IManageExtraFiles[] { subtitles }, TestLogger);
            Mocker.SetConstant<IExtraService>(extras);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.ImportExtraFiles).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.ExtraFileExtensions).Returns("srt");
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieFileDeletedEvent>())).Callback<MovieFileDeletedEvent>(subtitleFiles.Handle);
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieRenamedEvent>())).Callback<MovieRenamedEvent>(extras.Handle);
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieScannedEvent>())).Callback<MovieScannedEvent>(extras.Handle);
            _importer = Mocker.Resolve<ImportApprovedMovie>();
            Mocker.SetConstant<IImportApprovedMovie>(_importer);
        }

        private MovieEditionManagementService DefinitionManagement()
        {
            Mocker.SetConstant<IMovieEditionNamingPreviewService>(Mocker.Resolve<MovieEditionNamingPreviewService>());
            return Mocker.Resolve<MovieEditionManagementService>();
        }

        private MovieEditionDefinitionChange Definition(string name = null)
        {
            var target = _editions.Get(_directors.Id);
            var movie = _movies.Get(_movie.Id);
            return new MovieEditionDefinitionChange
            {
                MovieId = movie.Id, EditionId = target.Id, Name = name ?? target.Name,
                Attributes = new(target.Attributes), MatchRules = new(target.MatchRules),
                ExpectedName = target.Name, ExpectedRuleRevision = target.RuleRevision,
                ExpectedQualityProfileId = target.QualityProfileId, ExpectedMovieFileId = target.MovieFileId,
                ExpectedMovieMonitored = movie.Monitored, ExpectedEditionMonitored = target.Monitored
            };
        }

        [Test]
        public void definition_edit_preserves_files_until_an_explicit_rename()
        {
            ManualImport(_imax, "SyntheticDefinitionImax");
            ManualImport(_directors, "SyntheticDefinitionDirectors");
            var before = Db.All<MovieFile>();
            var request = Definition("Synthetic New Directors 3D");
            request.Attributes["branding"] = "Synthetic Collector";
            request.MatchRules.Add("SyntheticCollector$");
            var management = DefinitionManagement();
            var preview = management.PreviewDefinition(request);
            preview.EditingEnabled.Should().BeTrue();
            preview.CreationEnabled.Should().BeFalse();
            preview.Samples.Should().HaveCount(2);
            var saved = management.SetDefinition(request);
            saved.RuleRevision.Should().Be(request.ExpectedRuleRevision + 1);
            Db.All<MovieFile>().Should().BeEquivalentTo(before);
            before.Should().OnlyContain(f => File.Exists(Path.Combine(_movie.Path, f.RelativePath)));
            _recycled.Should().BeEmpty();
            Mocker.Resolve<RenameMovieFileService>().Execute(new RenameMovieCommand { MovieIds = new() { _movie.Id } });
            var renamed = Db.All<MovieFile>();
            renamed.Single(f => f.MovieEditionId == _directors.Id).RelativePath.Should().Contain("Synthetic New Directors 3D");
            renamed.Single(f => f.MovieEditionId == _imax.Id).RelativePath.Should().Be(before.Single(f => f.MovieEditionId == _imax.Id).RelativePath);
        }

        [Test]
        public void rules_only_edit_accepts_the_targets_existing_video_and_subtitles()
        {
            ManualImport(_imax, "SyntheticDefinitionImax");
            ManualImport(_directors, "SyntheticDefinitionDirectors");
            var request = Definition();
            request.MatchRules.Add("SyntheticNewRule$");
            var management = DefinitionManagement();
            management.PreviewDefinition(request).EditingEnabled.Should().BeTrue();
            management.SetDefinition(request).MovieFileId.Should().Be(request.ExpectedMovieFileId);
            Db.All<SubtitleFile>().Should().HaveCount(2);
            _recycled.Should().BeEmpty();
        }

        [TestCase("unassigned-video")]
        [TestCase("sidecar")]
        [TestCase("same-name")]
        [TestCase("sanitized-name")]
        [TestCase("naming-config")]
        public void definition_preview_and_save_reject_conflicts_before_any_state_or_file_change(string conflict)
        {
            ManualImport(_imax, "SyntheticDefinitionImax");
            ManualImport(_directors, "SyntheticDefinitionDirectors");
            var request = Definition("Synthetic Conflicting Cut");
            var management = DefinitionManagement();
            var preview = management.PreviewDefinition(request);
            preview.EditingEnabled.Should().BeTrue();
            var destination = Path.Combine(_movie.Path, preview.Samples.Single(s => s.MovieEditionId == _directors.Id).RelativePath);
            switch (conflict)
            {
                case "unassigned-video": File.WriteAllText(Path.ChangeExtension(destination, ".mp4"), "Synthetic reserved video"); break;
                case "sidecar": File.WriteAllText(Path.ChangeExtension(destination, ".en.srt"), "Synthetic reserved sidecar"); break;
                case "same-name": request.Name = _imax.Name; break;
                case "sanitized-name": request.Name = _imax.Name + "<>"; break;
                case "naming-config": _naming.StandardMovieFormat = "{Movie Title}"; break;
            }

            var before = Db.All<MovieEdition>();
            var files = Db.All<MovieFile>();
            management.PreviewDefinition(request).EditingEnabled.Should().BeFalse();
            Action edit = () => management.SetDefinition(request);
            edit.Should().Throw<InvalidOperationException>();
            Db.All<MovieEdition>().Should().BeEquivalentTo(before);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
            _recycled.Should().BeEmpty();
        }

        [Test]
        public void definition_preview_uses_each_existing_files_actual_quality()
        {
            ManualImport(_imax, "SyntheticDefinitionImax");
            ManualImport(_directors, "SyntheticDefinitionDirectors");
            var file = Db.All<MovieFile>().Single(f => f.MovieEditionId == _directors.Id);
            file.Quality = new QualityModel(Quality.HDTV720p);
            Db.Update(file);
            _naming.StandardMovieFormat = "{Managed Edition} {Quality Title}";
            var result = DefinitionManagement().PreviewDefinition(Definition());
            result.EditingEnabled.Should().BeTrue();
            result.Samples.Single(s => s.MovieEditionId == _directors.Id).RelativePath.Should().Contain("HDTV-720p");
            result.Samples.Single(s => s.MovieEditionId == _imax.Id).RelativePath.Should().Contain("Bluray-1080p");
        }

        private string Source(string marker)
        {
            var folder = Path.Combine(TempFolder, "downloads", marker);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Synthetic.Lunar.Voyage.2026-" + marker + ".mkv");
            File.WriteAllText(path, marker);
            File.WriteAllText(Path.ChangeExtension(path, ".en.srt"), "synthetic subtitle " + marker);
            return path;
        }

        private void ManualImport(MovieEdition edition, string marker)
        {
            Mocker.Resolve<ManualImportService>().Execute(new ManualImportCommand
            {
                ImportMode = ImportMode.Copy,
                Files = new List<ManualImportFile>
                {
                    new ManualImportFile { MovieId = _movie.Id, MovieEditionId = edition.Id, Path = Source(marker), Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English }, ReleaseGroup = "SyntheticGroup" }
                }
            });
        }

        private ManualImportFile ClientFile(MovieEdition edition, int clientId)
        {
            const string downloadId = "synthetic-shared-manual-id";
            var path = Source("SyntheticClient" + clientId);
            var tracked = new TrackedDownload
            {
                DownloadClient = clientId,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = clientId },
                    OutputPath = new OsPath(Path.GetDirectoryName(path))
                },
                RemoteMovie = new RemoteMovie { Movie = _movie, EditionContext = _contexts.Resolve(_movie, edition.Id) }
            };
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(clientId, downloadId)).Returns(tracked);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(clientId, downloadId)).Returns(new List<MovieHistory>());
            return new ManualImportFile
            {
                MovieId = _movie.Id, MovieEditionId = edition.Id, DownloadClientId = clientId, DownloadId = downloadId,
                Path = path, Quality = new QualityModel(Quality.Bluray1080p),
                Languages = new List<Language> { Language.English }, ReleaseGroup = "SyntheticGroup"
            };
        }

        [Test]
        public void manual_batch_should_settle_both_clients_with_the_same_download_id_independently()
        {
            var files = new List<ManualImportFile> { ClientFile(_imax, 41), ClientFile(_directors, 42) };
            Mocker.Resolve<ManualImportService>().Execute(new ManualImportCommand { ImportMode = ImportMode.Copy, Files = files });
            Db.All<MovieFile>().Should().HaveCount(2);
            Db.All<SubtitleFile>().Should().HaveCount(2);
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.Is<DownloadCompletedEvent>(e => e.TrackedDownload.DownloadClient == 41)), Times.Once());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.Is<DownloadCompletedEvent>(e => e.TrackedDownload.DownloadClient == 42)), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.Find(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IHistoryService>().Verify(s => s.FindByDownloadId(41, "synthetic-shared-manual-id"), Times.Once());
            Mocker.GetMock<IHistoryService>().Verify(s => s.FindByDownloadId(42, "synthetic-shared-manual-id"), Times.Once());
            Mocker.GetMock<IHistoryService>().Verify(s => s.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void wrong_saved_edition_in_later_manual_item_should_reject_entire_batch_before_import()
        {
            ManualImport(_imax, "SyntheticExistingImax");
            ManualImport(_directors, "SyntheticExistingDirectors");
            var ids = Db.All<MovieFile>().Select(f => f.Id).ToList();
            var files = new List<ManualImportFile> { ClientFile(_imax, 41), ClientFile(_directors, 42) };
            files[1].MovieEditionId = _imax.Id;
            _recycled.Clear();
            Action import = () => Mocker.Resolve<ManualImportService>().Execute(new ManualImportCommand { ImportMode = ImportMode.Copy, Files = files });
            import.Should().Throw<InvalidOperationException>().WithMessage("*saved assignment*");
            Db.All<MovieFile>().Select(f => f.Id).Should().BeEquivalentTo(ids);
            _recycled.Should().BeEmpty();
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [TestCase(0, "synthetic-shared-manual-id")]
        [TestCase(41, null)]
        [TestCase(999, "synthetic-shared-manual-id")]
        public void explicit_unknown_or_invalid_manual_client_should_reject_before_import(int clientId, string downloadId)
        {
            var file = ClientFile(_imax, 41);
            file.DownloadClientId = clientId;
            file.DownloadId = downloadId;
            Action import = () => Mocker.Resolve<ManualImportService>().Execute(new ManualImportCommand { Files = new List<ManualImportFile> { file } });
            import.Should().Throw<InvalidOperationException>();
            Db.All<MovieFile>().Should().BeEmpty();
            _recycled.Should().BeEmpty();
        }

        [Test]
        public void naming_preview_defaults_cut_and_preserves_attributes_without_creating_or_mutating_files()
        {
            ManualImport(_imax, "SyntheticPreviewImax");
            ManualImport(_directors, "SyntheticPreviewDirectors");
            var before = Db.All<MovieFile>().Select(f => (f.Id, f.RelativePath, f.MovieEditionId)).ToList();
            var proposal = new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Collector 3D IMAX",
                Attributes = new Dictionary<string, string> { { "framing", "imax" }, { "stereoscopic", "3d" }, { "custom-label", "Synthetic Collector" } },
                MatchRules = new List<string> { "SyntheticExclusiveGroup$" }
            };
            var result = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(proposal);
            result.NamingValid.Should().BeTrue();
            result.CreationEnabled.Should().BeTrue();
            result.Samples.Should().HaveCount(3);
            result.ProposedEdition.Attributes["cut"].Should().Be("theatrical");
            result.ProposedEdition.Attributes["stereoscopic"].Should().Be("3d");
            result.ProposedEdition.Attributes["custom-label"].Should().Be("Synthetic Collector");
            proposal.Attributes.Should().NotContainKey("cut");
            Db.All<MovieFile>().Select(f => (f.Id, f.RelativePath, f.MovieEditionId)).Should().BeEquivalentTo(before);
            _editions.GetByMovie(_movie.Id).Should().HaveCount(2);
            _recycled.Should().BeEmpty();
        }

        [TestCase(false, "{Movie Title} [{Managed Edition}]")]
        [TestCase(true, "{Movie Title}")]
        [TestCase(true, "{Movie Title} [{Managed Edition:10}]")]
        public void naming_preview_requires_enabled_renaming_and_a_full_managed_label(bool enabled, string format)
        {
            _naming.RenameMovies = enabled;
            _naming.StandardMovieFormat = format;
            var result = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Preview" });
            result.NamingValid.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Contains("{Managed Edition}"));
        }

        [Test]
        public void naming_preview_rejects_sanitized_collision_between_distinct_display_names()
        {
            ManualImport(_imax, "SyntheticPreviewImax");
            _imax.Name = "Synthetic/Variant";
            _editions.SetFields(_imax, e => e.Name);
            var result = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = @"Synthetic\Variant" });
            result.NamingValid.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Contains("same destination"));
            Db.All<MovieFile>().Should().HaveCount(1);
            _recycled.Should().BeEmpty();
        }

        [Test]
        public void naming_preview_does_not_refresh_media_information_for_naming_tokens()
        {
            ManualImport(_imax, "SyntheticPreviewImax");
            _naming.StandardMovieFormat += " {MediaInfo VideoCodec}";
            Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Preview" }).NamingValid.Should().BeTrue();
            Mocker.GetMock<IUpdateMediaInfo>().Verify(s => s.Update(It.IsAny<MovieFile>(), It.IsAny<Movie>()), Times.Never());
        }

        [Test]
        public void naming_preview_requires_an_owned_reference_file_when_the_movie_is_empty()
        {
            var result = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Preview" });
            result.NamingValid.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Contains("first edition file"));
        }

        [Test]
        public void naming_preview_rejects_an_unassigned_file_record_with_the_same_basename_and_another_extension()
        {
            ManualImport(_imax, "SyntheticPreviewImax");
            var service = Mocker.Resolve<MovieEditionNamingPreviewService>();
            var proposal = new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Preview" };
            var candidate = service.Preview(proposal).Samples.Last().RelativePath;
            Db.Insert(new MovieFile
            {
                MovieId = _movie.Id, RelativePath = Path.ChangeExtension(candidate, ".avi"), Size = 1,
                Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English },
                DateAdded = DateTime.UtcNow, ReleaseGroup = "SyntheticGroup"
            });
            var result = service.Preview(proposal);
            result.NamingValid.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Contains("sidecar basename"));
            Db.All<MovieFile>().Should().HaveCount(2);
            _recycled.Should().BeEmpty();
        }

        [Test]
        public void naming_preview_rejects_control_characters_in_a_proposed_label()
        {
            Action preview = () => Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "Synthetic\0Cut" });
            preview.Should().Throw<ArgumentException>();
            Db.All<MovieFile>().Should().BeEmpty();
            _editions.GetByMovie(_movie.Id).Should().HaveCount(2);
        }

        [Test]
        public void naming_preview_rejects_a_display_name_that_sanitizes_to_an_empty_label()
        {
            ManualImport(_imax, "SyntheticPreviewImax");
            var result = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "<>" });
            result.NamingValid.Should().BeFalse();
            result.Rejections.Should().Contain(r => r.Contains("usable filename label"));
        }

        [TestCase(31)]
        [TestCase(32)]
        public void naming_preview_bounds_attributes_after_defaulting_the_cut(int count)
        {
            ManualImport(_imax, "SyntheticAttributeLimit");
            var proposal = new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Attribute Limit",
                Attributes = Enumerable.Range(0, count).ToDictionary(i => "synthetic-" + i, _ => "Synthetic Value")
            };
            var preview = Mocker.Resolve<MovieEditionNamingPreviewService>();
            if (count == 31)
            {
                preview.Preview(proposal).ProposedEdition.Attributes.Should().HaveCount(32);
            }
            else
            {
                Action validate = () => preview.Preview(proposal);
                validate.Should().Throw<ArgumentException>();
            }
        }

        private MovieEdition CreateManaged(string name)
        {
            Mocker.SetConstant<IMovieEditionNamingPreviewService>(Mocker.Resolve<MovieEditionNamingPreviewService>());
            return Mocker.Resolve<MovieEditionManagementService>().Create(new MovieEdition
            {
                MovieId = _movie.Id, Name = name, Monitored = true, MovieFileId = _imax.MovieFileId,
                Attributes = new Dictionary<string, string> { { "stereoscopic", "3d" }, { "framing", "imax" }, { "custom-label", "Synthetic Collector" } }
            });
        }

        [Test]
        public void managed_creation_uses_fresh_preview_and_creates_only_an_unmonitored_empty_target()
        {
            ManualImport(_imax, "SyntheticCreationImax");
            ManualImport(_directors, "SyntheticCreationDirectors");
            var before = Db.All<MovieFile>().Select(f => (f.Id, f.RelativePath, f.MovieEditionId)).ToList();
            var created = CreateManaged("Synthetic Collector 3D IMAX");
            created.Id.Should().BePositive();
            created.EditionKey.Should().StartWith("custom-");
            created.Monitored.Should().BeFalse();
            created.MovieFileId.Should().BeNull();
            created.QualityProfileId.Should().Be(_movie.QualityProfileId);
            created.Attributes["cut"].Should().Be("theatrical");
            created.Attributes["stereoscopic"].Should().Be("3d");
            created.Attributes["framing"].Should().Be("imax");
            created.Attributes["custom-label"].Should().Be("Synthetic Collector");
            Db.All<MovieFile>().Select(f => (f.Id, f.RelativePath, f.MovieEditionId)).Should().BeEquivalentTo(before);
            Db.Single<Movie>().PrimaryEditionId.Should().Be(_imax.Id);
            _recycled.Should().BeEmpty();
        }

        [TestCase("Theatrical IMAX")]
        [TestCase("<>")]
        public void managed_creation_rejects_invalid_or_colliding_names_without_creating_a_target(string name)
        {
            ManualImport(_imax, "SyntheticCreationImax");
            Action create = () => CreateManaged(name);
            create.Should().Throw<InvalidOperationException>();
            _editions.GetByMovie(_movie.Id).Should().HaveCount(2);
            Db.All<MovieFile>().Should().ContainSingle();
            _recycled.Should().BeEmpty();
        }

        [TestCase(".avi")]
        [TestCase(".en.srt")]
        public void managed_creation_checks_unrecorded_disk_video_and_sidecar_conflicts(string extension)
        {
            ManualImport(_imax, "SyntheticCreationImax");
            var proposal = new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Collector 3D IMAX" };
            var path = Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(proposal).Samples.Last().RelativePath;
            var conflict = Path.Combine(_movie.Path, Path.ChangeExtension(path, null) + extension);
            File.WriteAllText(conflict, "Synthetic conflict bytes");
            Action create = () => CreateManaged(proposal.Name);
            create.Should().Throw<InvalidOperationException>();
            File.ReadAllText(conflict).Should().Be("Synthetic conflict bytes");
            _editions.GetByMovie(_movie.Id).Should().HaveCount(2);
            _recycled.Should().BeEmpty();
        }

        [Test]
        public void managed_creation_reruns_naming_validation_after_a_successful_preview()
        {
            ManualImport(_imax, "SyntheticCreationImax");
            Mocker.Resolve<MovieEditionNamingPreviewService>().Preview(new MovieEdition { MovieId = _movie.Id, Name = "Synthetic Preview" }).NamingValid.Should().BeTrue();
            _naming.RenameMovies = false;
            Action create = () => CreateManaged("Synthetic Preview");
            create.Should().Throw<InvalidOperationException>();
            _editions.GetByMovie(_movie.Id).Should().HaveCount(2);
        }

        [Test]
        public void two_manual_editions_should_survive_scan_rename_reload_and_independent_upgrades()
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            Db.All<MovieFile>().Should().HaveCount(2);
            Db.All<SubtitleFile>().Should().HaveCount(2);
            Mocker.Resolve<DiskScanService>().Scan(_movies.Get(_movie.Id));

            _naming.StandardMovieFormat = "{Movie Title} [{Managed Edition}] renamed";
            Mocker.Resolve<RenameMovieFileService>().Execute(new RenameMovieCommand { MovieIds = new List<int> { _movie.Id } });
            var renamed = Db.All<MovieFile>();
            renamed.Should().OnlyContain(f => f.RelativePath.Contains("renamed"));
            foreach (var file in renamed)
            {
                File.Exists(Path.Combine(_movie.Path, file.RelativePath)).Should().BeTrue();
                var subtitle = Db.All<SubtitleFile>().Single(s => s.MovieFileId == file.Id);
                File.Exists(Path.Combine(_movie.Path, subtitle.RelativePath)).Should().BeTrue();
            }

            // Re-open through fresh repositories/context service, as after a restart.
            var reloaded = new MovieEditionContextService(Mocker.Resolve<MovieEditionRepository>(), Mocker.Resolve<MediaFileRepository>(), Mocker.Resolve<IQualityProfileRepository>());
            reloaded.Resolve(_movie, _imax.Id).Edition.Attributes["framing"].Should().Be("imax");
            reloaded.Resolve(_movie, _directors.Id).Edition.Attributes["stereoscopic"].Should().Be("3d");
            foreach (var target in new[] { _imax, _directors })
            {
                var other = Db.All<MovieFile>().Single(f => f.MovieEditionId != target.Id);
                var otherPath = Path.Combine(_movie.Path, other.RelativePath);
                var otherContents = File.ReadAllText(otherPath);
                var otherSubtitle = Db.All<SubtitleFile>().Single(s => s.MovieFileId == other.Id);
                _recycled.Clear();
                ManualImport(target, "SyntheticUpgrade" + target.Id);
                Db.All<MovieFile>().Should().HaveCount(2).And.Contain(f => f.Id == other.Id);
                File.ReadAllText(otherPath).Should().Be(otherContents);
                Db.All<SubtitleFile>().Should().Contain(s => s.Id == otherSubtitle.Id);
                _recycled.Should().NotContain(otherPath);
            }

            Mocker.Resolve<DiskScanService>().Scan(_movies.Get(_movie.Id));
            Db.All<MovieFile>().Should().HaveCount(2);
            Db.All<SubtitleFile>().Should().HaveCount(2);
        }

        [Test]
        public void should_reject_using_another_editions_file_as_the_upgrade_source()
        {
            ManualImport(_imax, "SyntheticImax");
            var other = Db.Single<MovieFile>();
            var path = Path.Combine(_movie.Path, other.RelativePath);
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = path, EditionContext = _contexts.Resolve(_movie, _directors.Id), Quality = new QualityModel(Quality.Bluray1080p) };
            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Move);
            string.Join(" ", result.Single().Errors).Should().Contain("source file already belongs");
            File.ReadAllText(path).Should().Be("SyntheticImax");
            Db.Single<MovieFile>().Id.Should().Be(other.Id);
            _recycled.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_reject_stale_context_before_recycling_anything()
        {
            var stale = _contexts.Resolve(_movie, _imax.Id);
            ManualImport(_imax, "SyntheticCurrent");
            var current = Db.Single<MovieFile>();
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = Source("SyntheticStale"), EditionContext = stale, Quality = new QualityModel(Quality.Bluray1080p) };
            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Copy);
            string.Join(" ", result.Single().Errors).Should().Contain("changed");
            _recycled.Should().BeEmpty();
            Db.Single<MovieFile>().Id.Should().Be(current.Id);
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_transfer_should_preserve_other_edition_and_leave_target_recoverable(bool afterCopy)
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            var other = Db.All<MovieFile>().Single(f => f.MovieEditionId == _directors.Id);
            var otherSubtitle = Db.All<SubtitleFile>().Single(s => s.MovieFileId == other.Id);
            Mocker.GetMock<IDiskTransferService>().Setup(s => s.TransferFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TransferMode>(), It.IsAny<bool>()))
                .Returns<string, string, TransferMode, bool>((source, destination, mode, overwrite) =>
                {
                    if (afterCopy)
                    {
                        File.Copy(source, destination, overwrite);
                    }

                    throw new IOException("Synthetic transfer failure");
                });
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = Source("SyntheticFailure"), EditionContext = _contexts.Resolve(_movie, _imax.Id), Quality = new QualityModel(Quality.Bluray1080p) };
            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Copy);
            result.Single().Result.Should().Be(ImportResultType.Skipped);
            _editions.Get(_imax.Id).MovieFileId.Should().BeNull();
            Db.Single<MovieFile>().Id.Should().Be(other.Id);
            File.ReadAllText(Path.Combine(_movie.Path, other.RelativePath)).Should().Be("SyntheticDirectors");
            File.Exists(Path.Combine(_movie.Path, otherSubtitle.RelativePath)).Should().BeTrue();
            File.Exists(local.Path).Should().BeTrue();
            Directory.GetFiles(Path.Combine(TempFolder, "recycle")).Should().HaveCount(2);
            string.Join(" ", result.Single().Errors).Should().Contain("Synthetic transfer failure");
            if (afterCopy)
            {
                var transferred = Path.Combine(_movie.Path, "Synthetic Lunar Voyage [Theatrical IMAX].mkv");
                Mocker.Resolve<DiskScanService>().Scan(_movies.Get(_movie.Id));
                File.ReadAllText(transferred).Should().Be("SyntheticFailure");
                _editions.Get(_imax.Id).MovieFileId.Should().BeNull();
                Db.Single<MovieFile>().Id.Should().Be(other.Id);
            }
        }

        [Test]
        public void should_leave_unassigned_files_on_scan_and_match_extras_to_their_own_file()
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            var unknown = Path.Combine(_movie.Path, "Synthetic.Unassigned.2026.mkv");
            File.WriteAllText(unknown, "preserve synthetic unassigned file");
            Mocker.Resolve<DiskScanService>().Scan(_movies.Get(_movie.Id));
            File.Exists(unknown).Should().BeTrue();
            Db.All<MovieFile>().Should().HaveCount(2);
            var resolver = Mocker.Resolve<ExtraFileOwnerResolver>();
            foreach (var subtitle in Db.All<SubtitleFile>())
            {
                resolver.Resolve(_movie, Path.Combine(_movie.Path, subtitle.RelativePath)).Id.Should().Be(subtitle.MovieFileId);
            }

            resolver.Resolve(_movie, Path.Combine(_movie.Path, "ambiguous.en.srt")).Should().BeNull();
        }

        [Test]
        public void should_block_multi_edition_acquisition_and_custom_import_scripts()
        {
            var spec = Mocker.Resolve<NzbDrone.Core.DecisionEngine.Specifications.MultiEditionAcquisitionSpecification>();
            spec.IsSatisfiedBy(new RemoteMovie { Movie = _movie }, null).Accepted.Should().BeFalse();
            Mocker.GetMock<IConfigService>().SetupGet(c => c.UseScriptImport).Returns(true);
            var local = new LocalMovie { Movie = _movie, Path = Source("SyntheticScript"), EditionContext = _contexts.Resolve(_movie, _directors.Id), Quality = new QualityModel(Quality.Bluray1080p) };
            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Copy);
            string.Join(" ", result.Single().Errors).Should().Contain("scripts are disabled");
            _recycled.Should().BeEmpty();
            Db.All<MovieFile>().Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_reject_ambiguous_manual_assignment_before_any_file_changes()
        {
            var path = Source("SyntheticAmbiguous");
            Action import = () => Mocker.Resolve<ManualImportService>().Execute(new ManualImportCommand
            {
                Files = new List<ManualImportFile> { new ManualImportFile { MovieId = _movie.Id, Path = path } }
            });
            import.Should().Throw<InvalidOperationException>().WithMessage("*explicitly*");
            Db.All<MovieFile>().Should().BeEmpty();
            File.Exists(path).Should().BeTrue();
            _recycled.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tracked_import_context_replaces_only_its_assigned_edition(bool primaryTarget)
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            var target = primaryTarget ? _imax : _directors;
            var other = Db.All<MovieFile>().Single(f => f.MovieEditionId != target.Id);
            var otherPath = Path.Combine(_movie.Path, other.RelativePath);
            var bytes = File.ReadAllBytes(otherPath);
            var item = new DownloadClientItem { DownloadId = "synthetic-recovered-download", Title = "Synthetic.Voyage.2026-SyntheticGroup", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 9 } };
            var context = _contexts.Resolve(_movie, target.Id);
            Mocker.GetMock<ITrackedDownloadService>().Setup(t => t.Find(9, item.DownloadId))
                .Returns(new TrackedDownload { RemoteMovie = new RemoteMovie { Movie = _movie, EditionContext = context } });
            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(Array.Empty<IImportDecisionEngineSpecification>());
            var decisions = Mocker.Resolve<ImportDecisionMaker>().GetImportDecisions(new List<string> { Source("SyntheticRecoveredUpgrade") }, _movies.Get(_movie.Id), item, null, true);
            decisions.Should().ContainSingle();
            decisions[0].LocalMovie.Quality = new QualityModel(Quality.Bluray1080p);
            decisions[0].LocalMovie.Languages = new List<Language> { Language.English };
            decisions[0].LocalMovie.EditionContext.Edition.Id.Should().Be(target.Id);

            var result = _importer.Import(decisions, true, null, ImportMode.Copy);

            result.Single().Result.Should().Be(ImportResultType.Imported);
            Db.All<MovieFile>().Single(f => f.MovieEditionId != target.Id).Id.Should().Be(other.Id);
            File.ReadAllBytes(otherPath).Should().Equal(bytes);
            Db.All<MovieFile>().Single(f => f.MovieEditionId == target.Id).Id.Should().NotBe(context.CurrentFile.Id);
        }

        [Test]
        public void should_reject_import_collision_before_recycling_either_edition()
        {
            ManualImport(_imax, "SyntheticImax");
            var existing = Db.Single<MovieFile>();
            _naming.StandardMovieFormat = "{Movie Title} [Theatrical IMAX]";
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = Source("SyntheticCollision"), EditionContext = _contexts.Resolve(_movie, _directors.Id), Quality = new QualityModel(Quality.Bluray1080p) };
            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Copy);
            result.Single().Result.Should().Be(ImportResultType.Skipped);
            string.Join(" ", result.Single().Errors).Should().Contain("another file");
            _recycled.Should().BeEmpty();
            Db.Single<MovieFile>().Id.Should().Be(existing.Id);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_reject_existing_file_import_with_another_editions_basename()
        {
            ManualImport(_imax, "SyntheticImax");
            var existing = Db.Single<MovieFile>();
            var path = Path.ChangeExtension(Path.Combine(_movie.Path, existing.RelativePath), ".mp4");
            File.WriteAllText(path, "Synthetic conflicting existing file");
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = path, EditionContext = _contexts.Resolve(_movie, _directors.Id), Quality = new QualityModel(Quality.Bluray1080p) };

            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, false);

            result.Single().Result.Should().Be(ImportResultType.Skipped);
            string.Join(" ", result.Single().Errors).Should().Contain("another file");
            Db.Single<MovieFile>().Id.Should().Be(existing.Id);
            File.ReadAllText(path).Should().Be("Synthetic conflicting existing file");
            _recycled.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase(".mp4")]
        [TestCase(".MKV")]
        public void should_reject_unassigned_video_basename_before_recycling(string extension)
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            var before = Db.All<MovieFile>();
            var current = before.Single(f => f.MovieEditionId == _imax.Id);
            var conflict = Path.ChangeExtension(Path.Combine(_movie.Path, current.RelativePath), extension);
            if (File.Exists(conflict))
            {
                Assert.Ignore("Requires distinct paths on a case-sensitive filesystem.");
            }

            File.WriteAllText(conflict, "Synthetic unassigned video");
            var local = new LocalMovie { Movie = _movies.Get(_movie.Id), Path = Source("SyntheticUnassignedConflict"), EditionContext = _contexts.Resolve(_movie, _imax.Id), Quality = new QualityModel(Quality.Bluray1080p) };

            var result = _importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, ImportMode.Copy);

            result.Single().Result.Should().Be(ImportResultType.Skipped);
            string.Join(" ", result.Single().Errors).Should().Contain("basename");
            Db.All<MovieFile>().Select(f => f.Id).Should().BeEquivalentTo(before.Select(f => f.Id));
            File.ReadAllText(conflict).Should().Be("Synthetic unassigned video");
            _recycled.Should().BeEmpty();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_reject_rename_collisions_before_moving_any_file()
        {
            ManualImport(_imax, "SyntheticImax");
            ManualImport(_directors, "SyntheticDirectors");
            var original = Db.All<MovieFile>().Select(f => f.RelativePath).ToList();
            _naming.StandardMovieFormat = "{Movie Title}";
            Action rename = () => Mocker.Resolve<RenameMovieFileService>().Execute(new RenameMovieCommand { MovieIds = new List<int> { _movie.Id } });
            rename.Should().Throw<InvalidOperationException>().WithMessage("*collide*");
            Db.All<MovieFile>().Select(f => f.RelativePath).Should().BeEquivalentTo(original);
            original.Should().OnlyContain(path => File.Exists(Path.Combine(_movie.Path, path)));
        }
    }
}
