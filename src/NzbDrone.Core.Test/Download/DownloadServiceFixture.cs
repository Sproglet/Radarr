using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadServiceFixture : CoreTest<DownloadService>
    {
        private RemoteMovie _parseResult;
        private List<IDownloadClient> _downloadClients;
        [SetUp]
        public void Setup()
        {
            _downloadClients = new List<IDownloadClient>();

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClients(It.IsAny<bool>()))
                .Returns(_downloadClients);

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns<DownloadProtocol, int, bool, HashSet<int>>((v, i, f, t) => _downloadClients.FirstOrDefault(d => d.Protocol == v));

            var releaseInfo = Builder<ReleaseInfo>.CreateNew()
                .With(v => v.DownloadProtocol = DownloadProtocol.Usenet)
                .With(v => v.DownloadUrl = "http://test.site/download1.ext")
                .Build();

            _parseResult = Builder<RemoteMovie>.CreateNew()
                   .With(c => c.EditionRecoveryError = null)
                   .With(c => c.Movie = Builder<Movie>.CreateNew().Build())
                   .With(c => c.Release = releaseInfo)
                   .Build();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_or_failed_recovery_cannot_reach_download_client(bool recoveryFailure)
        {
            _parseResult.Release.Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup";
            if (recoveryFailure)
            {
                _parseResult.EditionRecoveryError = "Synthetic assignment needs review";
            }
            else
            {
                _parseResult.EditionContext = new MovieEditionContext(new MovieEdition { Id = 2 }, null, null, true);
            }

            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.DownloadReport(_parseResult, null));
            Mocker.GetMock<IProvideDownloadClient>().Verify(s => s.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task adding_a_target_cannot_overtake_an_authorized_submission_during_an_async_wait()
        {
            _parseResult.Release.Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup";
            var targets = new List<MovieEdition> { new() { Id = 1, MovieId = _parseResult.Movie.Id } };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(_parseResult.Movie.Id)).Returns(() => targets.ToList());
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Find(_parseResult.Movie.Id)).Returns(_parseResult.Movie);
            var proposed = new MovieEdition { MovieId = _parseResult.Movie.Id, Name = "Synthetic New 3D" };
            Mocker.GetMock<IMovieEditionNamingPreviewService>().Setup(p => p.Preview(proposed)).Returns(new MovieEditionNamingPreview
            {
                ProposedEdition = proposed,
                Samples = new() { new() { RelativePath = "Synthetic New 3D.mkv" } }
            });
            Mocker.GetMock<IMovieEditionRepository>()
                .Setup(r => r.CreateUnmonitored(proposed, _parseResult.Movie, It.IsAny<List<MovieEdition>>()))
                .Returns(() => { proposed.Id = 2; targets.Add(proposed); return proposed; });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mocker.GetMock<IRateLimitService>().Setup(r => r.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Returns(() => { entered.SetResult(); return resume.Task; });
            var client = WithUsenetClient();
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Returns(() => { targets.Should().ContainSingle(); return Task.FromResult("SyntheticSubmittedJob"); });
            var service = Mocker.Resolve<MovieEditionManagementService>();
            var download = Subject.DownloadReport(_parseResult, null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var creationStarted = new ManualResetEventSlim();
            var creation = Task.Run(() => { creationStarted.Set(); return service.Create(proposed); });
            try
            {
                creationStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                (await Task.WhenAny(creation, Task.Delay(100))).Should().NotBe(creation);
            }
            finally
            {
                resume.TrySetResult();
            }

            await Task.WhenAll(download, creation).WaitAsync(TimeSpan.FromSeconds(5));
            targets.Should().HaveCount(2);
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.DownloadReport(_parseResult, null));
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task acquisition_gate_is_released_before_synchronous_grab_subscribers()
        {
            WithUsenetClient().Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).ReturnsAsync("SyntheticSubmittedJob");
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieGrabbedEvent>())).Callback(() =>
            {
                var callback = Task.Run(() => { using var lease = MovieEditionAcquisitionGate.Acquire(_parseResult.Movie.Id); });
                callback.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
            });
            await Subject.DownloadReport(_parseResult, null).WaitAsync(TimeSpan.FromSeconds(5));
        }

        private Mock<IDownloadClient> WithUsenetClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Usenet);

            return mock;
        }

        private Mock<IDownloadClient> WithTorrentClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Torrent);

            return mock;
        }

        [Test]
        public async Task Download_report_should_publish_on_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            VerifyEventPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task Download_report_should_grab_using_client()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            mock.Verify(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public void Download_report_should_not_publish_on_failed_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Throws(new WebException());

            Assert.ThrowsAsync<WebException>(async () => await Subject.DownloadReport(_parseResult, null));

            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_indexer_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_with_long_time()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = "300";

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), TimeSpan.FromMinutes(5.0)), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_based_on_date()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = DateTime.UtcNow.AddSeconds(300).ToString("r");

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(),
                    It.IsInRange<TimeSpan>(TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1), Moq.Range.Inclusive)),
                    Times.Once());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_downloadclient_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Throws(new DownloadClientException("Some Error"));

            Assert.ThrowsAsync<DownloadClientException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_indexer_404_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseUnavailableException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void should_not_attempt_download_if_client_isnt_configured()
        {
            Assert.ThrowsAsync<DownloadClientUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadClient>().Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task should_attempt_download_even_if_client_is_disabled()
        {
            var mockUsenet = WithUsenetClient();

            Mocker.GetMock<IDownloadClientStatusService>()
                  .Setup(v => v.GetBlockedProviders())
                  .Returns(new List<DownloadClientStatus>
                  {
                      new DownloadClientStatus
                      {
                          ProviderId = _downloadClients.First().Definition.Id,
                          DisabledTill = DateTime.UtcNow.AddHours(3)
                      }
                  });

            await Subject.DownloadReport(_parseResult, null);

            Mocker.GetMock<IDownloadClientStatusService>().Verify(c => c.GetBlockedProviders(), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
            VerifyEventPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task should_send_download_to_correct_usenet_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task should_send_download_to_correct_torrent_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            _parseResult.Release.DownloadProtocol = DownloadProtocol.Torrent;

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }
    }
}
