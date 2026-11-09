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
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
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
