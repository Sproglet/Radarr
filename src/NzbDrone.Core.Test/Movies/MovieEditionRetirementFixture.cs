using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;

namespace NzbDrone.Core.Test.MovieTests
{
    public partial class MovieEditionRemovalFixture
    {
        private MovieHistory ManualHistory(MovieHistoryEventType type = MovieHistoryEventType.DownloadFolderImported) => new()
        {
            MovieId = _movie.Id, MovieEditionId = _target.Id, EditionRuleRevision = _target.RuleRevision,
            EventType = type, Date = DateTime.UtcNow, SourceTitle = "Synthetic.Lunar.Voyage.2026-SyntheticGroup",
            Quality = new(), Languages = new(), Data = new() { { "movieEditionName", "Synthetic Previous Collector Name" } }
        };

        [Test]
        public void retirement_preserves_manual_import_rename_and_delete_history_with_original_names()
        {
            foreach (var type in new[] { MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.MovieFileRenamed, MovieHistoryEventType.MovieFileDeleted })
            {
                Db.Insert(ManualHistory(type));
            }

            using var connection = Db.OpenConnection();
            var parent = (IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies"" WHERE ""Id"" = @Id", _movie);
            var history = Db.All<MovieHistory>();
            var files = Db.All<MovieFile>();
            var kept = Subject.GetByMovie(_movie.Id).Where(e => e.Id != _target.Id).ToList();

            Subject.RetireEmpty(Change());

            Subject.Find(_target.Id).Should().BeNull();
            Db.All<MovieHistory>().Should().BeEquivalentTo(history);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
            Subject.GetByMovie(_movie.Id).Should().BeEquivalentTo(kept);
            ((IDictionary<string, object>)connection.QuerySingle(@"SELECT * FROM ""Movies"" WHERE ""Id"" = @Id", _movie)).Should().BeEquivalentTo(parent);
        }

        [TestCase("foreign-movie")]
        [TestCase("missing-revision")]
        [TestCase("zero-revision")]
        [TestCase("missing-name")]
        [TestCase("empty-name")]
        [TestCase("operation")]
        [TestCase("empty-operation")]
        [TestCase("download")]
        [TestCase("client-id")]
        [TestCase("client-type")]
        [TestCase("client-name")]
        [TestCase("unknown-event")]
        [TestCase("folder-event")]
        [TestCase("failed-event")]
        [TestCase("ignored-event")]
        public void incomplete_ambiguous_or_client_related_history_blocks_retirement(string damage)
        {
            var row = ManualHistory();
            switch (damage)
            {
                case "foreign-movie": row.MovieId += 999; break;
                case "missing-revision": row.EditionRuleRevision = null; break;
                case "zero-revision": row.EditionRuleRevision = 0; break;
                case "missing-name": row.Data.Clear(); break;
                case "empty-name": row.Data["movieEditionName"] = " "; break;
                case "operation": row.EditionOperationId = Guid.NewGuid().ToString("D"); break;
                case "empty-operation": row.EditionOperationId = ""; break;
                case "download": row.DownloadId = "synthetic-download"; break;
                case "client-id": row.Data[MovieHistory.DOWNLOAD_CLIENT_ID] = "41"; break;
                case "client-type": row.Data["DownloadClient"] = "Synthetic Client"; break;
                case "client-name": row.Data["DownloadClientName"] = "Synthetic Client"; break;
                case "unknown-event": row.EventType = MovieHistoryEventType.Unknown; break;
                case "folder-event": row.EventType = MovieHistoryEventType.MovieFolderImported; break;
                case "failed-event": row.EventType = MovieHistoryEventType.DownloadFailed; break;
                case "ignored-event": row.EventType = MovieHistoryEventType.DownloadIgnored; break;
            }

            Db.Insert(row);
            var history = Db.All<MovieHistory>();

            Action retire = () => Subject.RetireEmpty(Change());

            retire.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(_target);
            Db.All<MovieHistory>().Should().BeEquivalentTo(history);
        }

        [TestCase("pending")]
        [TestCase("journal-prepared")]
        [TestCase("journal-settled")]
        [TestCase("movie-grab")]
        [TestCase("download-grab")]
        [TestCase("download-terminal")]
        public void manual_history_does_not_override_saved_acquisition_guards(string reference)
        {
            Db.Insert(ManualHistory());
            AddReference(reference);

            Action retire = () => Subject.RetireEmpty(Change());

            retire.Should().Throw<InvalidOperationException>();
            Subject.Get(_target.Id).Should().BeEquivalentTo(_target);
        }

        [TestCase("name")]
        [TestCase("primary")]
        [TestCase("monitored")]
        [TestCase("file")]
        [TestCase("retained-file")]
        public void retirement_revalidates_confirmation_and_file_eligibility(string condition)
        {
            Db.Insert(ManualHistory());
            var target = _target;
            if (condition == "primary") target = _primary;
            else if (condition == "monitored")
            {
                _target.Monitored = true;
                Subject.Update(_target);
            }
            else if (condition is "file" or "retained-file")
            {
                Attach(_target);
                if (condition == "retained-file")
                {
                    using var connection = Db.OpenConnection();
                    connection.Execute(@"UPDATE ""MovieEditions"" SET ""MovieFileId"" = NULL WHERE ""Id"" = @Id", _target);
                    _target.MovieFileId = null;
                }
            }

            var change = Change(target);
            if (condition == "name") change.ExpectedName += " stale";
            var before = Subject.GetByMovie(_movie.Id);
            var files = Db.All<MovieFile>();

            Action retire = () => Subject.RetireEmpty(change);

            retire.Should().Throw<InvalidOperationException>();
            Subject.GetByMovie(_movie.Id).Should().BeEquivalentTo(before);
            Db.All<MovieFile>().Should().BeEquivalentTo(files);
        }

        [Test]
        public void malformed_history_data_is_a_review_conflict_without_removing_the_target()
        {
            var row = Db.Insert(ManualHistory());
            using var connection = Db.OpenConnection();
            connection.Execute(@"UPDATE ""History"" SET ""Data"" = 'synthetic-invalid-json' WHERE ""Id"" = @Id", row);

            Action retire = () => Subject.RetireEmpty(Change());

            retire.Should().Throw<InvalidOperationException>().WithMessage("*unreadable history*");
            Subject.Find(_target.Id).Should().NotBeNull();
            connection.ExecuteScalar<string>(@"SELECT ""Data"" FROM ""History"" WHERE ""Id"" = @Id", row).Should().Be("synthetic-invalid-json");
        }

        [Test]
        public void retirement_service_publishes_after_releasing_locks_and_keeps_history()
        {
            Db.Insert(ManualHistory());
            Mocker.SetConstant<IMovieEditionRepository>(Subject);
            Mocker.SetConstant<IMovieRepository>(Mocker.Resolve<MovieRepository>());
            Mocker.GetMock<IEventAggregator>().Setup(e => e.PublishEvent(It.IsAny<MovieUpdatedEvent>())).Callback(() =>
            {
                var reentrant = System.Threading.Tasks.Task.Run(() =>
                {
                    using var acquisition = MovieEditionAcquisitionGate.Acquire(_movie.Id);
                    using var files = MovieFileOperationLock.Acquire(_movie.Id);
                    return Subject.GetByMovie(_movie.Id).Count;
                });
                reentrant.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                reentrant.Result.Should().Be(2);
            });

            Mocker.Resolve<MovieEditionManagementService>().RetireEmpty(Change());

            Db.All<MovieHistory>().Should().ContainSingle();
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<MovieUpdatedEvent>()), Times.Once());
        }
    }
}
