using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class AlreadyImportedSpecificationFixture : CoreTest<AlreadyImportedSpecification>
    {
        private const int FIRST_MOVIE_ID = 1;
        private const string TITLE = "Movie.Title.2018.720p.HDTV.x264-Radarr";

        private Movie _movie;
        private QualityModel _hdtv720p;
        private QualityModel _hdtv1080p;
        private RemoteMovie _remoteMovie;
        private List<MovieHistory> _history;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                    .With(m => m.Id = FIRST_MOVIE_ID)
                                    .With(m => m.MovieFileId = 1)
                                    .Build();

            _hdtv720p = new QualityModel(Quality.HDTV720p, new Revision(version: 1));
            _hdtv1080p = new QualityModel(Quality.HDTV1080p, new Revision(version: 1));

            _remoteMovie = new RemoteMovie
            {
                Movie = _movie,
                ParsedMovieInfo = new ParsedMovieInfo { Quality = _hdtv720p },
                Release = Builder<ReleaseInfo>.CreateNew()
                                              .Build()
            };

            _history = new List<MovieHistory>();

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(true);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.GetByMovieId(It.IsAny<int>(), null))
                  .Returns(_history);
        }

        private void GivenCdhDisabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(false);
        }

        private void GivenHistoryItem(string downloadId, string sourceTitle, QualityModel quality, MovieHistoryEventType eventType)
        {
            _history.Add(new MovieHistory
                         {
                             DownloadId = downloadId,
                             SourceTitle = sourceTitle,
                             Quality = quality,
                             Date = DateTime.UtcNow,
                             EventType = eventType
                         });
        }

        [Test]
        public void should_be_accepted_if_CDH_is_disabled()
        {
            GivenCdhDisabled();

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_movie_does_not_have_a_file()
        {
            _remoteMovie.Movie.MovieFileId = 0;

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_movie_does_not_have_grabbed_event()
        {
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_movie_does_not_have_imported_event()
        {
            GivenHistoryItem(Guid.NewGuid().ToString().ToUpper(), TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_grabbed_and_imported_quality_is_the_same()
        {
            var downloadId = Guid.NewGuid().ToString().ToUpper();

            GivenHistoryItem(downloadId, TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);
            GivenHistoryItem(downloadId, TITLE, _hdtv720p, MovieHistoryEventType.DownloadFolderImported);

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_grabbed_download_id_and_release_torrent_hash_is_unknown()
        {
            var downloadId = Guid.NewGuid().ToString().ToUpper();

            GivenHistoryItem(downloadId, TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);
            GivenHistoryItem(downloadId, TITLE, _hdtv1080p, MovieHistoryEventType.DownloadFolderImported);

            _remoteMovie.Release = Builder<TorrentInfo>.CreateNew()
                                                         .With(t => t.DownloadProtocol = DownloadProtocol.Torrent)
                                                         .With(t => t.InfoHash = null)
                                                         .Build();

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_accepted_if_grabbed_download_does_not_have_an_id()
        {
            var downloadId = Guid.NewGuid().ToString().ToUpper();

            GivenHistoryItem(null, TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);
            GivenHistoryItem(downloadId, TITLE, _hdtv1080p, MovieHistoryEventType.DownloadFolderImported);

            _remoteMovie.Release = Builder<TorrentInfo>.CreateNew()
                                                         .With(t => t.DownloadProtocol = DownloadProtocol.Torrent)
                                                         .With(t => t.InfoHash = downloadId)
                                                         .Build();

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_rejected_if_grabbed_download_id_matches_release_torrent_hash()
        {
            var downloadId = Guid.NewGuid().ToString().ToUpper();

            GivenHistoryItem(downloadId, TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);
            GivenHistoryItem(downloadId, TITLE, _hdtv1080p, MovieHistoryEventType.DownloadFolderImported);

            _remoteMovie.Release = Builder<TorrentInfo>.CreateNew()
                                                         .With(t => t.DownloadProtocol = DownloadProtocol.Torrent)
                                                         .With(t => t.InfoHash = downloadId)
                                                         .Build();

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_be_rejected_if_release_title_matches_grabbed_event_source_title()
        {
            var downloadId = Guid.NewGuid().ToString().ToUpper();

            GivenHistoryItem(downloadId, TITLE, _hdtv720p, MovieHistoryEventType.Grabbed);
            GivenHistoryItem(downloadId, TITLE, _hdtv1080p, MovieHistoryEventType.DownloadFolderImported);

            _remoteMovie.Release = Builder<TorrentInfo>.CreateNew()
                                                         .With(t => t.DownloadProtocol = DownloadProtocol.Torrent)
                                                         .With(t => t.InfoHash = downloadId)
                                                         .Build();

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeFalse();
        }

        private const string SyntheticTitle = "Synthetic.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup";

        private void GivenEdition(bool hasFile = true)
        {
            _remoteMovie.Release = new ReleaseInfo { Title = SyntheticTitle, DownloadProtocol = DownloadProtocol.Usenet, IndexerId = 5, Indexer = "Synthetic Indexer" };
            _remoteMovie.EditionContext = new MovieEditionContext(new MovieEdition { Id = 2, MovieId = _movie.Id }, null,
                hasFile ? new MovieFile { Id = 20 } : null, true);
            Mocker.GetMock<IMovieEditionRepository>().Setup(s => s.GetByMovie(_movie.Id)).Returns(new List<MovieEdition>
            {
                new MovieEdition { Id = 1, MovieId = _movie.Id, MovieFileId = 10 },
                new MovieEdition { Id = 2, MovieId = _movie.Id, MovieFileId = hasFile ? 20 : null }
            });
        }

        private MovieHistory EditionHistory(int? editionId, MovieHistoryEventType type, string downloadId, int seconds, string title = SyntheticTitle, string clientId = "9")
        {
            var row = new MovieHistory { MovieId = _movie.Id, MovieEditionId = editionId, EventType = type, DownloadId = downloadId,
                Date = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds), SourceTitle = title,
                Quality = type == MovieHistoryEventType.Grabbed ? _hdtv1080p : _hdtv720p };
            row.Data[MovieHistory.DOWNLOAD_CLIENT_ID] = clientId;
            if (type == MovieHistoryEventType.DownloadFolderImported && editionId.HasValue)
            {
                row.Data["fileId"] = (editionId == 1 ? 10 : 20).ToString();
            }
            _history.Add(row);
            return row;
        }

        [Test]
        public void newest_other_edition_does_not_hide_selected_edition_import_loop()
        {
            GivenEdition();
            EditionHistory(2, MovieHistoryEventType.Grabbed, "synthetic-secondary", 1);
            EditionHistory(2, MovieHistoryEventType.DownloadFolderImported, "synthetic-secondary", 2);
            EditionHistory(1, MovieHistoryEventType.Grabbed, "synthetic-primary", 3, "Synthetic.Voyage.2026.Theatrical.IMAX.1080p.BluRay-SyntheticGroup");
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeFalse();
        }

        [Test]
        public void primary_history_does_not_form_secondary_import_loop()
        {
            GivenEdition();
            EditionHistory(1, MovieHistoryEventType.Grabbed, "synthetic-primary", 1);
            EditionHistory(1, MovieHistoryEventType.DownloadFolderImported, "synthetic-primary", 2);
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void loop_guard_uses_selected_file_availability(bool secondaryHasFile)
        {
            GivenEdition(secondaryHasFile);
            _movie.MovieFileId = secondaryHasFile ? 0 : 10;
            EditionHistory(2, MovieHistoryEventType.Grabbed, "synthetic-secondary", 1);
            EditionHistory(2, MovieHistoryEventType.DownloadFolderImported, "synthetic-secondary", 2);
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().Be(!secondaryHasFile);
        }

        [TestCase("edition")]
        [TestCase("client")]
        [TestCase("unassigned")]
        [TestCase("older-import")]
        public void unrelated_terminal_event_cannot_complete_selected_grab(string difference)
        {
            GivenEdition();
            EditionHistory(2, MovieHistoryEventType.Grabbed, "synthetic-reused-id", 2);
            EditionHistory(difference == "edition" ? 1 : difference == "unassigned" ? null : 2,
                MovieHistoryEventType.DownloadFolderImported, "synthetic-reused-id", difference == "older-import" ? 1 : 3,
                clientId: difference == "client" ? "10" : "9");
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }

        [Test]
        public void imported_torrent_is_suppressed_across_editions_even_if_selected_file_is_missing()
        {
            GivenEdition(false);
            var hash = "abcdef0123456789abcdef0123456789abcdef01";
            _remoteMovie.Release = new TorrentInfo { InfoHash = hash, DownloadProtocol = DownloadProtocol.Torrent, Title = SyntheticTitle };
            var grab = EditionHistory(1, MovieHistoryEventType.Grabbed, hash.ToUpperInvariant(), 1);
            grab.Data["protocol"] = "2";
            EditionHistory(1, MovieHistoryEventType.DownloadFolderImported, hash.ToUpperInvariant(), 2);
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeFalse();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void imported_guid_deduplication_is_global_across_editions_and_scoped_to_indexer(bool otherIndexer)
        {
            GivenEdition(false);
            _remoteMovie.Release.Guid = "synthetic-guid";
            var grab = EditionHistory(1, MovieHistoryEventType.Grabbed, "synthetic-primary", 1);
            grab.Data["protocol"] = "1";
            grab.Data["guid"] = "synthetic-guid";
            grab.Data["indexerId"] = otherIndexer ? "6" : "5";
            EditionHistory(1, MovieHistoryEventType.DownloadFolderImported, "synthetic-primary", 2);
            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().Be(otherIndexer);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void historical_release_does_not_block_reacquiring_a_lost_or_superseded_file(bool superseded)
        {
            GivenEdition(false);
            _remoteMovie.Release.Guid = "synthetic-guid";
            var editionId = superseded ? 1 : 2;
            var grab = EditionHistory(editionId, MovieHistoryEventType.Grabbed, "synthetic-historical", 1);
            grab.Data["protocol"] = "1";
            grab.Data["guid"] = "synthetic-guid";
            grab.Data["indexerId"] = "5";
            var imported = EditionHistory(editionId, MovieHistoryEventType.DownloadFolderImported, "synthetic-historical", 2);
            if (superseded)
            {
                imported.Data["fileId"] = "9"; // The primary now owns a different file (10).
            }

            Subject.IsSatisfiedBy(_remoteMovie, new()).Accepted.Should().BeTrue();
        }
    }
}
