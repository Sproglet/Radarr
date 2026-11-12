using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class EditionSubmissionJournalFixture
    {
        private MovieEditionSubmissionService Coordinator()
        {
            Mocker.SetConstant<IMovieEditionSubmissionRepository>(_journal);
            Mocker.SetConstant<IMovieRepository>(Mocker.Resolve<MovieRepository>());
            Mocker.SetConstant<IMovieEditionRepository>(Mocker.Resolve<MovieEditionRepository>());
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            Mocker.SetConstant<IMediaFileRepository>(Mocker.Resolve<MediaFileRepository>());
            return Mocker.Resolve<MovieEditionSubmissionService>();
        }

        private RemoteMovie SubmissionRelease(MovieEdition edition = null)
        {
            var intent = Intent();
            return new RemoteMovie
            {
                Movie = _movie, Release = intent.Release, ParsedMovieInfo = intent.ParsedMovieInfo,
                Languages = new() { NzbDrone.Core.Languages.Language.English }, CustomFormatScore = 77,
                EditionContext = new MovieEditionContext(edition ?? _edition, null, null, true)
            };
        }

        private static Mock<IDownloadClient> SubmissionClient()
        {
            var client = new Mock<IDownloadClient>();
            client.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 41, Name = "Synthetic Client" });
            client.SetupGet(c => c.Name).Returns("Synthetic Client Implementation");
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).ReturnsAsync("synthetic-download");
            return client;
        }

        [Test]
        public async Task coordinator_prepares_durable_intent_before_io_and_sends_a_frozen_request()
        {
            var service = Coordinator();
            var release = SubmissionRelease();
            var title = release.Release.Title;
            var client = SubmissionClient();
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).Returns<RemoteMovie, IIndexer>((sent, _) =>
            {
                var prepared = _journal.All().Single();
                prepared.State.Should().Be(MovieEditionSubmissionState.Prepared);
                prepared.DownloadId.Should().BeNull();
                Mocker.Resolve<HistoryRepository>().Count().Should().Be(0);
                Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(0);
                sent.EditionContext.Edition.Id.Should().Be(_edition.Id);
                sent.EffectiveQualityProfile.Id.Should().Be(_edition.QualityProfileId);
                sent.Release.Should().NotBeSameAs(release.Release);
                release.Release.Title = "Synthetic.Mutated.Voyage.2026-SyntheticGroup";
                release.Languages.Clear();
                sent.Release.Title.Should().Be(title);
                sent.Languages.Should().HaveCount(1);
                return Task.FromResult("synthetic-download");
            });

            var saved = await service.Submit(release, client.Object, null);

            saved.State.Should().Be(MovieEditionSubmissionState.Recorded);
            Mocker.Resolve<HistoryRepository>().All().Single().SourceTitle.Should().Be(title);
            Mocker.Resolve<DownloadHistoryRepository>().All().Single().EditionOperationId.Should().Be(saved.OperationId);
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task ambiguous_client_exception_retains_intent_and_cannot_resubmit()
        {
            var service = Coordinator();
            var release = SubmissionRelease();
            var client = SubmissionClient();
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).ThrowsAsync(new IOException("Synthetic unknown client outcome"));
            Func<Task> send = () => service.Submit(release, client.Object, null);

            await send.Should().ThrowAsync<IOException>();
            var saved = _journal.All().Single();
            saved.State.Should().Be(MovieEditionSubmissionState.Prepared);
            saved.DownloadId.Should().BeNull();
            await send.Should().ThrowAsync<ModelConflictException>();
            Action reconcile = () => service.Reconcile(saved.OperationId);
            reconcile.Should().Throw<InvalidOperationException>();
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public async Task missing_client_identity_retains_ambiguity(string downloadId)
        {
            var service = Coordinator();
            var client = SubmissionClient();
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).ReturnsAsync(downloadId);
            Func<Task> send = () => service.Submit(SubmissionRelease(), client.Object, null);

            await send.Should().ThrowAsync<InvalidOperationException>();
            _journal.All().Single().State.Should().Be(MovieEditionSubmissionState.Prepared);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(0);
        }

        private void RejectDownloadGrab(bool enabled)
        {
            using var connection = Db.OpenConnection();
            if (Db.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.Execute(enabled ? @"ALTER TABLE ""DownloadHistory"" ADD CONSTRAINT synthetic_reject_grab CHECK (""EventType"" <> 1)" :
                    @"ALTER TABLE ""DownloadHistory"" DROP CONSTRAINT synthetic_reject_grab");
            }
            else
            {
                connection.Execute(enabled ? @"CREATE TRIGGER synthetic_reject_grab BEFORE INSERT ON ""DownloadHistory"" WHEN NEW.""EventType"" = 1 BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END" :
                    "DROP TRIGGER synthetic_reject_grab");
            }
        }

        [Test]
        public async Task accepted_client_outcome_survives_database_failure_and_reconciles_without_resubmission()
        {
            var service = Coordinator();
            var client = SubmissionClient();
            RejectDownloadGrab(true);
            Func<Task> send = () => service.Submit(SubmissionRelease(), client.Object, null);

            await send.Should().ThrowAsync<System.Data.Common.DbException>();
            var saved = _journal.All().Single();
            saved.State.Should().Be(MovieEditionSubmissionState.Accepted);
            saved.DownloadId.Should().Be("synthetic-download");
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(0);
            Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(0);
            RejectDownloadGrab(false);

            service.Reconcile(saved.OperationId).State.Should().Be(MovieEditionSubmissionState.Recorded);
            service.Reconcile(saved.OperationId).State.Should().Be(MovieEditionSubmissionState.Recorded);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(1);
            Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(1);
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task failure_to_save_client_identity_retains_prepared_ambiguity_without_retry()
        {
            var service = Coordinator();
            var client = SubmissionClient();
            using (var connection = Db.OpenConnection())
            {
                connection.Execute(Db.DatabaseType == DatabaseType.PostgreSQL ?
                    @"ALTER TABLE ""MovieEditionSubmissions"" ADD CONSTRAINT synthetic_no_accept CHECK (""State"" <> 1)" :
                    @"CREATE TRIGGER synthetic_no_accept BEFORE UPDATE ON ""MovieEditionSubmissions"" WHEN NEW.""State"" = 1 BEGIN SELECT RAISE(ABORT, 'synthetic acceptance write failure'); END");
            }
            Func<Task> send = () => service.Submit(SubmissionRelease(), client.Object, null);

            await send.Should().ThrowAsync<System.Data.Common.DbException>();
            _journal.All().Single().State.Should().Be(MovieEditionSubmissionState.Prepared);
            _journal.All().Single().DownloadId.Should().BeNull();
            await send.Should().ThrowAsync<ModelConflictException>();
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task persisted_intent_is_retained_if_a_legacy_writer_changes_context_before_io()
        {
            var client = SubmissionClient();
            var actualMovies = Mocker.Resolve<MovieRepository>();
            var changingMovies = new Mock<IMovieRepository>();
            changingMovies.Setup(m => m.Get(_movie.Id)).Returns(() =>
            {
                using var connection = Db.OpenConnection();
                connection.Execute(@"UPDATE ""Movies"" SET ""Path"" = '/synthetic/moved' WHERE ""Id"" = @Id", _movie);
                return actualMovies.Get(_movie.Id);
            });
            var service = new MovieEditionSubmissionService(_journal, changingMovies.Object, Mocker.Resolve<MovieEditionRepository>(),
                Mocker.Resolve<QualityProfileRepository>(), Mocker.Resolve<MediaFileRepository>());
            Func<Task> send = () => service.Submit(SubmissionRelease(), client.Object, null);

            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*before client IO*");
            _journal.All().Single().State.Should().Be(MovieEditionSubmissionState.Prepared);
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public async Task different_editions_can_submit_independently_without_changing_the_parent_projection()
        {
            var service = Coordinator();
            var client = SubmissionClient();
            var count = 0;
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).Returns(() => Task.FromResult("synthetic-download-" + ++count));
            var first = await service.Submit(SubmissionRelease(), client.Object, null);
            var primary = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
            var second = await service.Submit(SubmissionRelease(primary), client.Object, null);

            first.MovieEditionId.Should().NotBe(second.MovieEditionId);
            _journal.Count().Should().Be(2);
            Mocker.Resolve<HistoryRepository>().Count().Should().Be(2);
            Mocker.Resolve<DownloadHistoryRepository>().Count().Should().Be(2);
            Mocker.Resolve<MovieRepository>().Get(_movie.Id).PrimaryEditionId.Should().Be(_movie.PrimaryEditionId);
        }

        [Test]
        public async Task concurrent_same_target_submission_sends_only_one_client_request()
        {
            var service = Coordinator();
            var client = SubmissionClient();
            using var start = new ManualResetEventSlim();
            client.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>())).Returns(async () =>
            {
                await Task.Delay(30);
                return "synthetic-download";
            });
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                start.Wait();
                try { await service.Submit(SubmissionRelease(), client.Object, null); return true; }
                catch (ModelConflictException) { return false; }
            })).ToArray();
            start.Set();

            var results = await Task.WhenAll(tasks);

            results.Count(sent => sent).Should().Be(1);
            _journal.Count().Should().Be(1);
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task stale_definition_fails_before_client_io_without_creating_intent()
        {
            var service = Coordinator();
            var release = SubmissionRelease();
            release.EditionContext.Edition.Name = "Synthetic Stale Name";
            var client = SubmissionClient();
            Func<Task> send = () => service.Submit(release, client.Object, null);

            await send.Should().ThrowAsync<InvalidOperationException>();
            _journal.Count().Should().Be(0);
            client.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }
    }
}
