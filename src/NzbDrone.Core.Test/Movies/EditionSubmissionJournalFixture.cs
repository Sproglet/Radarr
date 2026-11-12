using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class EditionSubmissionJournalFixture : DbTest
    {
        private Movie _movie;
        private MovieEdition _edition;
        private MovieEditionSubmissionRepository _journal;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<NzbDrone.Core.CustomFormats.ICustomFormatService>().Setup(s => s.All()).Returns(new List<NzbDrone.Core.CustomFormats.CustomFormat>());
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Profile", Items = new(), Language = NzbDrone.Core.Languages.Language.English });
            var metadata = Db.Insert(new MovieMetadata { Title = "Synthetic Voyage", TmdbId = 80808080, Year = 2026 });
            _movie = Mocker.Resolve<MovieRepository>().Insert(new Movie { MovieMetadataId = metadata.Id, Path = "/synthetic/voyage", QualityProfileId = profile.Id, Monitored = true });
            _edition = Mocker.Resolve<MovieEditionRepository>().Insert(new MovieEdition
            {
                MovieId = _movie.Id, Name = "Synthetic Directors 3D", EditionKey = "synthetic-directors-3d", Monitored = true,
                QualityProfileId = profile.Id, Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }
            });
            _journal = Mocker.Resolve<MovieEditionSubmissionRepository>();
        }

        private MovieEditionSubmission Intent() => new()
        {
            MovieId = _movie.Id, MovieEditionId = _edition.Id, EditionRuleRevision = _edition.RuleRevision,
            EditionName = _edition.Name, EditionAttributes = new(_edition.Attributes), QualityProfileId = _edition.QualityProfileId,
            MovieFileId = _edition.MovieFileId, MoviePath = _movie.Path, DownloadClientId = 41, DownloadClientName = "Synthetic Client",
            Release = new ReleaseInfo { Title = "Synthetic.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", Guid = "synthetic-release", DownloadUrl = "http://127.0.0.1/synthetic-only" },
            ParsedMovieInfo = new ParsedMovieInfo { SimpleReleaseTitle = "A.Movie.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", Quality = new(), Languages = new() }
        };

        private void Grabs(MovieEditionSubmission intent, bool movieHistory = true, bool downloadHistory = true)
        {
            if (movieHistory)
            {
                Mocker.Resolve<HistoryRepository>().Insert(new MovieHistory
                {
                    MovieId = intent.MovieId, MovieEditionId = intent.MovieEditionId, EditionRuleRevision = intent.EditionRuleRevision,
                    EditionOperationId = intent.OperationId, DownloadId = intent.DownloadId, SourceTitle = intent.Release.Title,
                    Date = DateTime.UtcNow, EventType = MovieHistoryEventType.Grabbed, Quality = new(), Languages = new(),
                    Data = new() { { MovieHistory.DOWNLOAD_CLIENT_ID, intent.DownloadClientId.ToString() }, { "movieEditionName", intent.EditionName } }
                });
            }

            if (downloadHistory)
            {
                Mocker.Resolve<DownloadHistoryRepository>().Insert(new DownloadHistory
                {
                    MovieId = intent.MovieId, MovieEditionId = intent.MovieEditionId, EditionRuleRevision = intent.EditionRuleRevision,
                    EditionOperationId = intent.OperationId, DownloadId = intent.DownloadId, DownloadClientId = intent.DownloadClientId,
                    Date = DateTime.UtcNow, EventType = DownloadHistoryEventType.DownloadGrabbed, SourceTitle = intent.Release.Title,
                    Data = new() { { "movieEditionName", intent.EditionName } }
                });
            }
        }

        [Test]
        public void prepared_snapshot_survives_reload_and_definition_changes_without_reclassifying()
        {
            var intent = _journal.Prepare(Intent());
            intent.State.Should().Be(MovieEditionSubmissionState.Prepared);
            Guid.ParseExact(intent.OperationId, "D").Should().NotBe(Guid.Empty);
            _edition.Attributes["framing"] = "imax";
            _edition.Name = "Synthetic Changed Edition";
            Mocker.Resolve<MovieEditionRepository>().Update(_edition);

            var saved = Mocker.Resolve<MovieEditionSubmissionRepository>().Get(intent.Id);

            saved.OperationId.Should().Be(intent.OperationId);
            saved.EditionRuleRevision.Should().Be(1);
            saved.EditionName.Should().Be("Synthetic Directors 3D");
            saved.EditionAttributes.Should().BeEquivalentTo(new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } });
            saved.Release.Guid.Should().Be("synthetic-release");
            saved.ParsedMovieInfo.SimpleReleaseTitle.Should().Be(intent.ParsedMovieInfo.SimpleReleaseTitle);
            saved.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        }

        [TestCase("movie")]
        [TestCase("edition")]
        [TestCase("revision")]
        [TestCase("name")]
        [TestCase("attributes")]
        [TestCase("profile")]
        [TestCase("file")]
        [TestCase("path")]
        [TestCase("client")]
        [TestCase("state")]
        [TestCase("download")]
        [TestCase("operation")]
        public void stale_or_incomplete_intent_rolls_back_its_entire_insert(string change)
        {
            var intent = Intent();
            switch (change)
            {
                case "movie": intent.MovieId += 999; break;
                case "edition": intent.MovieEditionId += 999; break;
                case "revision": intent.EditionRuleRevision++; break;
                case "name": intent.EditionName = "Synthetic Different Name"; break;
                case "attributes": intent.EditionAttributes.Clear(); break;
                case "profile": intent.QualityProfileId += 999; break;
                case "file": intent.MovieFileId = 999999; break;
                case "path": intent.MoviePath += "/changed"; break;
                case "client": intent.DownloadClientId = 0; break;
                case "state": intent.State = MovieEditionSubmissionState.Accepted; break;
                case "download": intent.DownloadId = "synthetic-accepted"; break;
                case "operation": intent.OperationId = "not-a-guid"; break;
            }

            Action prepare = () => _journal.Prepare(intent);
            prepare.Should().Throw<InvalidOperationException>();
            _journal.Count().Should().Be(0);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_journal_blocks_target_and_parent_removal(bool accepted)
        {
            var intent = _journal.Prepare(Intent());
            if (accepted) _journal.Accept(intent.OperationId, "synthetic-download");

            Action removeTarget = () => Mocker.Resolve<MovieEditionRepository>().Delete(_edition.Id);
            Action removeMovie = () => Mocker.Resolve<MovieRepository>().Delete(_movie.Id);

            removeTarget.Should().Throw<InvalidOperationException>();
            removeMovie.Should().Throw<ModelConflictException>();
            Mocker.Resolve<MovieEditionRepository>().Get(_edition.Id).Should().NotBeNull();
            _journal.Get(intent.Id).State.Should().Be(accepted ? MovieEditionSubmissionState.Accepted : MovieEditionSubmissionState.Prepared);
        }

        [Test]
        public void blocked_movie_removal_does_not_publish_file_deletion_events(
            [Values(false, true)] bool deleteFiles,
            [Values(false, true)] bool bulk)
        {
            _journal.Prepare(Intent());
            Mocker.SetConstant<IMovieRepository>(Mocker.Resolve<MovieRepository>());
            var movies = Mocker.Resolve<MovieService>();
            Action remove = () =>
            {
                if (bulk) movies.DeleteMovies(new() { _movie.Id }, deleteFiles);
                else movies.DeleteMovie(_movie.Id, deleteFiles);
            };

            remove.Should().Throw<ModelConflictException>();
            Mocker.GetMock<NzbDrone.Core.Messaging.Events.IEventAggregator>()
                .Verify(e => e.PublishEvent(Moq.It.IsAny<MoviesDeletedEvent>()), Moq.Times.Never());
        }

        [Test]
        public void a_different_edition_can_retain_an_independent_prepared_operation()
        {
            var first = _journal.Prepare(Intent());
            var primary = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
            var other = Intent();
            other.MovieEditionId = primary.Id;
            other.EditionName = primary.Name;
            other.EditionAttributes = primary.Attributes;
            other.EditionRuleRevision = primary.RuleRevision;

            var second = _journal.Prepare(other);

            _journal.Count().Should().Be(2);
            second.OperationId.Should().NotBe(first.OperationId);
            second.MovieEditionId.Should().NotBe(first.MovieEditionId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void prepared_intent_cannot_skip_acceptance_evidence(bool settle)
        {
            var intent = _journal.Prepare(Intent());
            Action skip = () => { if (settle) _journal.Settle(intent.OperationId); else _journal.Record(intent.OperationId); };

            skip.Should().Throw<InvalidOperationException>();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Prepared);
        }

        [Test]
        public void concurrent_preparation_keeps_only_one_unsettled_operation()
        {
            var requests = new[] { Intent(), Intent() };
            using var start = new ManualResetEventSlim();
            var tasks = requests.Select(request => Task.Run(() =>
            {
                start.Wait();
                try { _journal.Prepare(request); return true; }
                catch (System.Data.Common.DbException) { return false; }
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks);

            tasks.Count(t => t.Result).Should().Be(1);
            _journal.Count().Should().Be(1);
        }

        [Test]
        public void acceptance_is_idempotent_and_cannot_reassign_the_saved_client_download()
        {
            var intent = _journal.Prepare(Intent());
            _journal.Accept(intent.OperationId, "synthetic-download");
            _journal.Accept(intent.OperationId, "synthetic-download").State.Should().Be(MovieEditionSubmissionState.Accepted);
            Action conflict = () => _journal.Accept(intent.OperationId, "synthetic-other");
            conflict.Should().Throw<InvalidOperationException>();
            _journal.Get(intent.Id).DownloadId.Should().Be("synthetic-download");
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void recording_requires_both_durable_grab_streams(bool movieHistory, bool downloadHistory)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            Grabs(intent, movieHistory, downloadHistory);

            Action record = () => _journal.Record(intent.OperationId);

            record.Should().Throw<InvalidOperationException>();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Accepted);
        }

        [Test]
        public void matching_settlement_releases_target_but_retains_durable_evidence(
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType eventType,
            [Values("matching", "operation", "revision", "client", "download", "target", "movie")] string evidence)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            Grabs(intent);
            _journal.Record(intent.OperationId);
            var terminal = new DownloadHistory
            {
                MovieId = intent.MovieId, MovieEditionId = intent.MovieEditionId, EditionRuleRevision = intent.EditionRuleRevision,
                EditionOperationId = intent.OperationId, DownloadClientId = intent.DownloadClientId, DownloadId = intent.DownloadId,
                SourceTitle = intent.Release.Title, Date = DateTime.UtcNow.AddSeconds(1), EventType = eventType
            };
            switch (evidence)
            {
                case "operation": terminal.EditionOperationId = Guid.NewGuid().ToString("D"); break;
                case "revision": terminal.EditionRuleRevision++; break;
                case "client": terminal.DownloadClientId++; break;
                case "download": terminal.DownloadId += "-other"; break;
                case "target": terminal.MovieEditionId++; break;
                case "movie": terminal.MovieId++; break;
            }
            Mocker.Resolve<DownloadHistoryRepository>().Insert(terminal);
            Action settle = () => _journal.Settle(intent.OperationId);
            if (evidence == "matching")
            {
                settle.Should().NotThrow();
                _journal.Settle(intent.OperationId).State.Should().Be(MovieEditionSubmissionState.Settled);
                Mocker.Resolve<MovieEditionRepository>().Delete(_edition.Id);
                _journal.Get(intent.Id).EditionName.Should().Be(intent.EditionName);
            }
            else
            {
                settle.Should().Throw<InvalidOperationException>();
                _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Recorded);
            }
        }

        [Test]
        public void mismatched_durable_grab_cannot_advance_the_journal(
            [Values("History", "DownloadHistory")] string table,
            [Values("MovieId", "MovieEditionId", "EditionRuleRevision", "DownloadId", "SourceTitle", "Data")] string field)
        {
            var intent = _journal.Prepare(Intent());
            intent = _journal.Accept(intent.OperationId, "synthetic-download");
            Grabs(intent);
            using (var connection = Db.OpenConnection())
            {
                // Restore/reconciliation must reject historical corruption too.
                var value = field switch
                {
                    "DownloadId" or "SourceTitle" => (object)"synthetic-other",
                    "Data" => "{\"downloadClientId\":\"41\",\"movieEditionName\":\"Synthetic Changed Name\"}",
                    _ => 999999
                };
                connection.Execute($@"UPDATE ""{table}"" SET ""{field}"" = @Value WHERE ""EditionOperationId"" = @OperationId",
                    new { Value = value, intent.OperationId });
            }

            Action record = () => _journal.Record(intent.OperationId);
            record.Should().Throw<InvalidOperationException>();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Accepted);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void monitoring_must_still_allow_preparation(bool movieMaster)
        {
            var intent = Intent();
            using (var connection = Db.OpenConnection())
            {
                var table = movieMaster ? "Movies" : "MovieEditions";
                connection.Execute($@"UPDATE ""{table}"" SET ""Monitored"" = @Monitored", new { Monitored = false });
            }

            Action prepare = () => _journal.Prepare(intent);
            prepare.Should().Throw<InvalidOperationException>();
            _journal.Count().Should().Be(0);
        }

        [TestCase("update")]
        [TestCase("bulk")]
        [TestCase("partial")]
        [TestCase("delete")]
        public void ordinary_writers_cannot_rewrite_or_remove_journal_evidence(string writer)
        {
            var intent = _journal.Prepare(Intent());
            intent.State = MovieEditionSubmissionState.Settled;
            Action mutate = writer switch
            {
                "update" => () => _journal.Update(intent),
                "bulk" => () => _journal.UpdateMany(new[] { intent }),
                "partial" => () => _journal.SetFields(intent, x => x.State),
                _ => () => _journal.Delete(intent.Id)
            };

            mutate.Should().Throw<InvalidOperationException>();
            _journal.Get(intent.Id).State.Should().Be(MovieEditionSubmissionState.Prepared);
        }

        [Test]
        public void journal_timestamp_columns_use_utc_on_postgresql()
        {
            if (Db.DatabaseType != DatabaseType.PostgreSQL) return;
            using var connection = Db.OpenConnection();
            connection.Query<string>(@"SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema()
                AND table_name = 'MovieEditionSubmissions' AND column_name IN ('CreatedAt','UpdatedAt')")
                .Should().HaveCount(2).And.OnlyContain(t => t == "timestamp with time zone");
        }
    }
}
