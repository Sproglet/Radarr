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
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Find(_parseResult.Movie.Id)).Returns(() => _parseResult.Movie);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_journal_blocks_legacy_client_selection_too(bool explicitClient)
        {
            Mocker.GetMock<IMovieEditionSubmissionRepository>().Setup(r => r.HasUnsettled(_parseResult.Movie.Id, null)).Returns(true);
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.DownloadReport(_parseResult, explicitClient ? 41 : null));
            Mocker.GetMock<IProvideDownloadClient>().Verify(p => p.Get(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IProvideDownloadClient>().Verify(p => p.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()), Times.Never());
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
            var targets = new List<MovieEdition> { new() { Id = 1, MovieId = _parseResult.Movie.Id,
                QualityProfileId = _parseResult.Movie.QualityProfileId, Monitored = _parseResult.Movie.Monitored } };
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

        [TestCase("profile")]
        [TestCase("monitoring")]
        [TestCase("attributes")]
        [TestCase("rules")]
        public void independently_managed_single_target_cannot_bypass_submission_guard(string preference)
        {
            _parseResult.Movie.Monitored = true;
            var target = new MovieEdition { Id = 1, MovieId = _parseResult.Movie.Id, Monitored = true, QualityProfileId = _parseResult.Movie.QualityProfileId };
            if (preference == "profile") target.QualityProfileId++;
            if (preference == "monitoring") target.Monitored = false;
            if (preference == "attributes") target.Attributes["cut"] = "theatrical";
            if (preference == "rules") target.MatchRules.Add("SyntheticGroup$");
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(_parseResult.Movie.Id)).Returns(new List<MovieEdition> { target });
            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.DownloadReport(_parseResult, null));
            Mocker.GetMock<IProvideDownloadClient>().Verify(s => s.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        private Movie CopyAcquisitionState()
        {
            var movie = _parseResult.Movie;
            return new Movie { Id = movie.Id, MovieMetadataId = movie.MovieMetadataId, QualityProfileId = movie.QualityProfileId,
                PrimaryEditionId = movie.PrimaryEditionId, MovieFileId = movie.MovieFileId, Monitored = movie.Monitored, Path = movie.Path,
                Tags = movie.Tags == null ? new HashSet<int>() : new HashSet<int>(movie.Tags) };
        }

        [Test]
        public async Task stale_or_missing_movie_cannot_reach_client_submission(
            [Values("missing", "profile", "primary", "file", "monitoring", "path", "metadata", "tags", "identity", "remote")] string change,
            [Values(false, true)] bool duringWait)
        {
            _parseResult.Release.Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup";
            var current = CopyAcquisitionState();
            var evaluatedProfile = _parseResult.Movie.QualityProfileId;
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Find(_parseResult.Movie.Id)).Returns(() => current);
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.GetByMovie(_parseResult.Movie.Id)).Returns(new List<MovieEdition>
                { new() { Id = 1, MovieId = _parseResult.Movie.Id, QualityProfileId = evaluatedProfile, Monitored = _parseResult.Movie.Monitored } });
            var client = WithUsenetClient();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mocker.GetMock<IRateLimitService>().Setup(r => r.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Returns(() => { entered.SetResult(); return resume.Task; });
            Task operation = null;
            if (duringWait)
            {
                operation = Subject.DownloadReport(_parseResult, null);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (change == "missing") current = null;
            if (change == "profile") current.QualityProfileId++;
            if (change == "primary") current.PrimaryEditionId = (current.PrimaryEditionId ?? 0) + 1;
            if (change == "file") current.MovieFileId++;
            if (change == "monitoring") current.Monitored = !current.Monitored;
            if (change == "path") current.Path += "/SyntheticChanged";
            if (change == "metadata") current.MovieMetadataId++;
            if (change == "tags") current.Tags.Add(999999);
            if (change == "identity") current.Id++;
            if (change == "remote") _parseResult.Movie.QualityProfileId++;
            resume.TrySetResult();
            operation ??= Subject.DownloadReport(_parseResult, null);
            Func<Task> submit = () => operation;

            await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed after release evaluation*");

            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
            if (!duringWait)
            {
                Mocker.GetMock<IProvideDownloadClient>().Verify(c => c.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()), Times.Never());
            }

            VerifyEventNotPublished<MovieGrabbedEvent>();
            if (change != "remote") _parseResult.Movie.QualityProfileId.Should().Be(evaluatedProfile);
        }

        [Test]
        public async Task detached_unchanged_parent_with_same_tags_in_another_order_keeps_legacy_submission()
        {
            _parseResult.Release.Title = "Synthetic.Voyage.2026.1080p.BluRay-SyntheticGroup";
            _parseResult.Movie.Tags = new HashSet<int> { 101, 102 };
            var current = CopyAcquisitionState();
            current.Tags = new HashSet<int> { 102, 101 };
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Find(_parseResult.Movie.Id)).Returns(current);
            WithUsenetClient().Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).ReturnsAsync("SyntheticSubmittedJob");

            await Subject.DownloadReport(_parseResult, null);

            Mocker.GetMock<IMovieRepository>().Verify(r => r.Find(current.Id), Times.Exactly(2));
            VerifyEventPublished<MovieGrabbedEvent>();
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
