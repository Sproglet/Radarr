using System.Collections.Generic;
using System;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private Movie _movie;
        private TrackedDownload _trackedDownload;
        private List<MovieHistory> _historyItems;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().Build();

            var remoteMovie = Builder<RemoteMovie>.CreateNew()
                                                      .With(r => r.Movie = _movie)
                                                      .Build();

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                         .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(t => t.RemoteMovie = remoteMovie)
                                                       .With(t => t.DownloadItem = downloadItem)
                                                       .Build();

            _historyItems = new List<MovieHistory>();
        }

        public void GivenHistoryForMovie(Movie movie, params MovieHistoryEventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                _historyItems.Add(
                    Builder<MovieHistory>.CreateNew()
                                            .With(h => h.MovieId = movie.Id)
                                            .With(h => h.EventType = eventType)
                                            .Build());
            }
        }

        [Test]
        public void should_return_false_if_there_is_no_history()
        {
            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_single_movie_download_is_not_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_true_if_single_movie_download_is_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }

        [TestCase("same", true)]
        [TestCase("client", false)]
        [TestCase("edition", false)]
        [TestCase("missing-client", false)]
        [TestCase("download", false)]
        public void completed_edition_requires_its_own_download_and_client_provenance(string difference, bool expected)
        {
            _trackedDownload.DownloadClient = 9;
            _trackedDownload.DownloadItem.DownloadId = "synthetic-shared";
            _trackedDownload.DownloadItem.Title = "Synthetic.Voyage.2026-SyntheticGroup";
            _trackedDownload.RemoteMovie.EditionContext = new MovieEditionContext(new MovieEdition { Id = 2, MovieId = _movie.Id }, null, null, true);
            var grab = new MovieHistory { MovieId = _movie.Id, MovieEditionId = 2, DownloadId = "synthetic-shared",
                Date = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), EventType = MovieHistoryEventType.Grabbed };
            grab.Data[MovieHistory.DOWNLOAD_CLIENT_ID] = "9";
            var imported = new MovieHistory { MovieId = _movie.Id, MovieEditionId = difference == "edition" ? 1 : 2,
                DownloadId = difference == "download" ? "synthetic-other" : "synthetic-shared",
                Date = grab.Date.AddSeconds(1), EventType = MovieHistoryEventType.DownloadFolderImported };
            if (difference != "missing-client")
            {
                imported.Data[MovieHistory.DOWNLOAD_CLIENT_ID] = difference == "client" ? "10" : "9";
            }

            Subject.IsImported(_trackedDownload, new List<MovieHistory> { imported, grab }).Should().Be(expected);
        }
    }
}
