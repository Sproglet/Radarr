using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.Qualities;

namespace NzbDrone.Core.Test.HistoryTests
{
    public class HistoryServiceFixture : CoreTest<HistoryService>
    {
        private QualityProfile _profile;
        private QualityProfile _profileCustom;

        [SetUp]
        public void Setup()
        {
            _profile = new QualityProfile { Cutoff = Quality.WEBDL720p.Id, Items = QualityFixture.GetDefaultQualities() };
            _profileCustom = new QualityProfile { Cutoff = Quality.WEBDL720p.Id, Items = QualityFixture.GetDefaultQualities(Quality.DVD) };
        }

        [Test]
        public void should_not_infer_a_processed_download_when_terminal_history_has_no_edition()
        {
            var quality = new QualityModel(Quality.Bluray1080p);
            Mocker.GetMock<IHistoryRepository>().Setup(r => r.FindDownloadHistory(42, quality)).Returns(new List<MovieHistory>
            {
                new MovieHistory { MovieId = 42, MovieEditionId = 2, DownloadId = "synthetic-download", EventType = MovieHistoryEventType.Grabbed },
                new MovieHistory { MovieId = 42, DownloadId = "synthetic-download", EventType = MovieHistoryEventType.DownloadFailed }
            });
            var local = new LocalMovie { Movie = new Movie { Id = 42 }, Path = "/synthetic/source.mkv",
                EditionContext = new MovieEditionContext(new MovieEdition { Id = 2 }, null, null, true) };
            var file = new MovieFile { MovieId = 42, Quality = quality, Path = local.Path };
            Subject.FindDownloadId(new MovieFileImportedEvent(local, file, new List<DeletedMovieFile>(), true, null)).Should().BeNull();
        }

        [Test]
        public void should_return_null_if_no_history()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel>());

            var quality = Subject.GetBestQualityInHistory(_profile, 2);

            quality.Should().BeNull();
        }

        [Test]
        public void should_return_best_quality()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel> { new QualityModel(Quality.DVD), new QualityModel(Quality.Bluray1080p) });

            var quality = Subject.GetBestQualityInHistory(_profile, 2);

            quality.Should().Be(new QualityModel(Quality.Bluray1080p));
        }

        [Test]
        public void should_return_best_quality_with_custom_order()
        {
            Mocker.GetMock<IHistoryRepository>()
                .Setup(v => v.GetBestQualityInHistory(2))
                .Returns(new List<QualityModel> { new QualityModel(Quality.DVD), new QualityModel(Quality.Bluray1080p) });

            var quality = Subject.GetBestQualityInHistory(_profileCustom, 2);

            quality.Should().Be(new QualityModel(Quality.DVD));
        }

        [Test]
        public void should_use_file_name_for_source_title_if_scene_name_is_null()
        {
            var movie = Builder<Movie>.CreateNew().Build();
            var movieFile = Builder<MovieFile>.CreateNew()
                                                  .With(f => f.SceneName = null)
                                                  .Build();

            var localMovie = new LocalMovie()
            {
                Movie = movie,
                Path = @"C:\Test\Unsorted\Movie.2011.mkv"
            };

            var downloadClientItem = new DownloadClientItem
            {
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = DownloadProtocol.Usenet,
                    Id = 1,
                    Name = "sab"
                },
                DownloadId = "abcd"
            };

            Subject.Handle(new MovieFileImportedEvent(localMovie, movieFile, new List<DeletedMovieFile>(), true, downloadClientItem));

            Mocker.GetMock<IHistoryRepository>()
                .Verify(v => v.Insert(It.Is<MovieHistory>(h => h.SourceTitle == Path.GetFileNameWithoutExtension(localMovie.Path))));
        }

        [TestCase(true, "assigned")]
        [TestCase(false, "assigned")]
        [TestCase(true, "unassigned")]
        [TestCase(false, "unassigned")]
        [TestCase(true, "missing")]
        [TestCase(false, "missing")]
        [TestCase(true, "foreign")]
        [TestCase(false, "foreign")]
        [TestCase(true, "stale-context")]
        [TestCase(false, "stale-context")]
        public void file_events_record_the_file_assignment_and_only_matching_target_metadata(bool deleted, string state)
        {
            var movie = new Movie { Id = 42, Path = Path.Combine(Path.GetTempPath(), "SyntheticHistory"), PrimaryEditionId = 1 };
            var file = new MovieFile
            {
                Id = 22, MovieId = movie.Id, Movie = movie, MovieEditionId = state == "unassigned" ? null : 2,
                RelativePath = "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D-SyntheticGroup.mkv",
                Quality = new QualityModel(Quality.Bluray1080p), Languages = new() { Language.English }, ReleaseGroup = "SyntheticGroup"
            };
            file.Path = Path.Combine(movie.Path, file.RelativePath);
            var edition = new MovieEdition { Id = 2, MovieId = state == "foreign" ? 99 : movie.Id, Name = "Synthetic Directors Cut 3D", RuleRevision = 9 };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns(state == "missing" ? null : edition);
            if (state == "stale-context")
            {
                file.EditionContext = new MovieEditionContext(new MovieEdition { Id = 1, MovieId = movie.Id, Name = "Synthetic Wrong Primary", RuleRevision = 3 }, null, null);
            }

            MovieHistory recorded = null;
            Mocker.GetMock<IHistoryRepository>().Setup(r => r.Insert(It.IsAny<MovieHistory>()))
                .Callback<MovieHistory>(h => recorded = h).Returns<MovieHistory>(h => h);
            if (deleted)
            {
                Subject.Handle(new MovieFileDeletedEvent(file, DeleteMediaFileReason.Manual));
            }
            else
            {
                Subject.Handle(new MovieFileRenamedEvent(movie, file, Path.Combine(movie.Path, "SyntheticOriginal-SyntheticGroup.mkv")));
            }

            recorded.Should().NotBeNull();
            recorded.MovieId.Should().Be(movie.Id);
            recorded.MovieEditionId.Should().Be(file.MovieEditionId);
            var hasMetadata = state == "assigned" || state == "stale-context";
            recorded.EditionRuleRevision.Should().Be(hasMetadata ? 9 : null);
            recorded.Data.GetValueOrDefault("movieEditionName").Should().Be(hasMetadata ? edition.Name : null);
            recorded.EventType.Should().Be(deleted ? MovieHistoryEventType.MovieFileDeleted : MovieHistoryEventType.MovieFileRenamed);
            if (state == "unassigned")
            {
                Mocker.GetMock<IMovieEditionRepository>().Verify(r => r.Find(It.IsAny<int>()), Times.Never());
            }
        }
    }
}
