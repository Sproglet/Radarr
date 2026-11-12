using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    public class EditionGrabImmutabilityFixture : DbTest
    {
        private Movie _movie;
        private MovieEdition _edition;

        [SetUp]
        public void Setup()
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Profile", Items = new(), Language = NzbDrone.Core.Languages.Language.English });
            _movie = Mocker.Resolve<MovieRepository>().Insert(new Movie
            {
                Title = "Synthetic Voyage",
                Path = "/synthetic/voyage", QualityProfileId = profile.Id, Tags = new()
            });
            _edition = Mocker.Resolve<MovieEditionRepository>().Get(_movie.PrimaryEditionId.Value);
        }

        private MovieHistory MovieGrab() => new()
        {
            MovieId = _movie.Id, MovieEditionId = _edition.Id, EditionRuleRevision = 1,
            EditionOperationId = Guid.NewGuid().ToString("D"),
            DownloadId = "synthetic-download", SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup",
            Date = DateTime.UtcNow, EventType = MovieHistoryEventType.Grabbed,
            Quality = new(), Languages = new(),
            Data = new() { { MovieHistory.DOWNLOAD_CLIENT_ID, "41" }, { "movieEditionName", _edition.Name } }
        };

        private DownloadHistory DownloadGrab() => new()
        {
            MovieId = _movie.Id, MovieEditionId = _edition.Id, EditionRuleRevision = 1,
            EditionOperationId = Guid.NewGuid().ToString("D"),
            DownloadId = "synthetic-download", SourceTitle = "Synthetic.Voyage.2026-SyntheticGroup",
            Date = DateTime.UtcNow, EventType = DownloadHistoryEventType.DownloadGrabbed, DownloadClientId = 41,
            Data = new() { { "movieEditionName", _edition.Name } }
        };

        private static Expression<Func<T, object>> Field<T>(string name)
        {
            var row = Expression.Parameter(typeof(T), "row");
            return Expression.Lambda<Func<T, object>>(Expression.Convert(Expression.Property(row, name), typeof(object)), row);
        }

        private static void Write<T>(BasicRepository<T> repository, T record, string field, string writer)
            where T : ModelBase, new()
        {
            switch (writer)
            {
                case "update": repository.Update(record); break;
                case "bulk": repository.UpdateMany(new[] { record }); break;
                case "partial": repository.SetFields(record, Field<T>(field)); break;
                case "bulk-partial": repository.SetFields(new[] { record }, Field<T>(field)); break;
                default: throw new ArgumentException(writer);
            }
        }

        private static string Change<T>(T record, string change)
            where T : ModelBase, new()
        {
            var type = typeof(T);
            if (change == "client" || change == "name")
            {
                if (change == "client" && record is DownloadHistory download)
                {
                    download.DownloadClientId++;
                    return nameof(DownloadHistory.DownloadClientId);
                }

                var data = (Dictionary<string, string>)type.GetProperty("Data").GetValue(record);
                data[change == "client" ? MovieHistory.DOWNLOAD_CLIENT_ID : "movieEditionName"] = "synthetic-changed";
                return "Data";
            }

            var field = change switch { "unassigned" => "MovieEditionId", "unknown-revision" => "EditionRuleRevision", "unknown-operation" => "EditionOperationId", _ => change };
            var property = type.GetProperty(field);
            var value = property.GetValue(record);
            property.SetValue(record, change switch
            {
                "MovieId" => (int)value + 999,
                "MovieEditionId" => (int)value + 999,
                "EditionRuleRevision" => (int)value + 1,
                "EditionOperationId" => Guid.NewGuid().ToString("D"),
                "DownloadId" => "synthetic-other-download",
                "SourceTitle" => "Synthetic.Other.Voyage.2026-SyntheticGroup",
                "Date" => ((DateTime)value).AddSeconds(-1),
                "EventType" => Enum.ToObject(property.PropertyType, 4),
                "unassigned" or "unknown-revision" or "unknown-operation" => null,
                _ => throw new ArgumentException(change)
            });
            return field;
        }

        private static void RejectMutation<T>(BasicRepository<T> repository, T record, string change, string writer)
            where T : ModelBase, new()
        {
            repository.Insert(record);
            var before = System.Text.Json.JsonSerializer.Serialize(repository.Get(record.Id));
            var field = Change(record, change);

            Action write = () => Write(repository, record, field, writer);

            write.Should().Throw<InvalidOperationException>().WithMessage("*grab identity*");
            System.Text.Json.JsonSerializer.Serialize(repository.Get(record.Id)).Should().Be(before);
        }

        [Test]
        public void typed_grab_identity_is_immutable_through_every_repository_writer(
            [Values(false, true)] bool downloadHistory,
            [Values("MovieId", "MovieEditionId", "unassigned", "EditionRuleRevision", "unknown-revision", "EditionOperationId", "unknown-operation", "DownloadId", "SourceTitle", "Date", "EventType", "client", "name")] string change,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            if (downloadHistory)
            {
                RejectMutation(Mocker.Resolve<DownloadHistoryRepository>(), DownloadGrab(), change, writer);
            }
            else
            {
                RejectMutation(Mocker.Resolve<HistoryRepository>(), MovieGrab(), change, writer);
            }
        }

        private static void Enrich<T>(BasicRepository<T> repository, T record, string writer)
            where T : ModelBase, new()
        {
            repository.Insert(record);
            var data = (Dictionary<string, string>)typeof(T).GetProperty("Data").GetValue(record);
            data["syntheticNote"] = "Synthetic enrichment";

            Write(repository, record, "Data", writer);

            var stored = (Dictionary<string, string>)typeof(T).GetProperty("Data").GetValue(repository.Get(record.Id));
            stored["syntheticNote"].Should().Be("Synthetic enrichment");
        }

        [Test]
        public void operation_linked_terminal_event_identity_is_immutable(
            [Values(false, true)] bool downloadHistory,
            [Values(2, 3, 4)] int terminal,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            if (downloadHistory)
            {
                var record = DownloadGrab();
                record.EventType = (DownloadHistoryEventType)terminal;
                RejectMutation(Mocker.Resolve<DownloadHistoryRepository>(), record, "EditionOperationId", writer);
            }
            else
            {
                var record = MovieGrab();
                record.EventType = terminal == 2 ? MovieHistoryEventType.DownloadFolderImported : terminal == 3 ? MovieHistoryEventType.DownloadFailed : MovieHistoryEventType.DownloadIgnored;
                RejectMutation(Mocker.Resolve<HistoryRepository>(), record, "EditionOperationId", writer);
            }
        }

        [Test]
        public void unrelated_grab_metadata_can_still_be_enriched(
            [Values(false, true)] bool downloadHistory,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            if (downloadHistory)
            {
                Enrich(Mocker.Resolve<DownloadHistoryRepository>(), DownloadGrab(), writer);
            }
            else
            {
                Enrich(Mocker.Resolve<HistoryRepository>(), MovieGrab(), writer);
            }
        }

        private static void RejectBatch<T>(BasicRepository<T> repository, T first, T second, bool partial)
            where T : ModelBase, new()
        {
            typeof(T).GetProperty("DownloadId").SetValue(second, "synthetic-second");
            repository.InsertMany(new[] { first, second });
            var data = (Dictionary<string, string>)typeof(T).GetProperty("Data").GetValue(first);
            data["syntheticNote"] = "Should roll back";
            Change(second, "Date");
            Action write = () =>
            {
                if (partial)
                {
                    repository.SetFields(new[] { first, second }, Field<T>("Data"), Field<T>("Date"));
                }
                else
                {
                    repository.UpdateMany(new[] { first, second });
                }
            };

            write.Should().Throw<InvalidOperationException>().WithMessage("*grab identity*");

            var stored = (Dictionary<string, string>)typeof(T).GetProperty("Data").GetValue(repository.Get(first.Id));
            stored.Should().NotContainKey("syntheticNote");
        }

        [Test]
        public void rejected_batch_rolls_back_prior_valid_metadata_changes(
            [Values(false, true)] bool downloadHistory,
            [Values(false, true)] bool partial)
        {
            if (downloadHistory)
            {
                RejectBatch(Mocker.Resolve<DownloadHistoryRepository>(), DownloadGrab(), DownloadGrab(), partial);
            }
            else
            {
                RejectBatch(Mocker.Resolve<HistoryRepository>(), MovieGrab(), MovieGrab(), partial);
            }
        }

        private static void RejectLegacyAssignment<T>(BasicRepository<T> repository, T record, string writer)
            where T : ModelBase, new()
        {
            var target = typeof(T).GetProperty("MovieEditionId");
            target.SetValue(record, null);
            typeof(T).GetProperty("EditionOperationId").SetValue(record, null);
            repository.Insert(record);
            target.SetValue(record, int.MaxValue);

            Action write = () => Write(repository, record, "MovieEditionId", writer);

            write.Should().Throw<InvalidOperationException>().WithMessage("*must belong*");
            target.GetValue(repository.Get(record.Id)).Should().BeNull();
        }

        [Test]
        public void assigning_a_legacy_grab_still_requires_a_valid_owner(
            [Values(false, true)] bool downloadHistory,
            [Values("update", "bulk", "partial", "bulk-partial")] string writer)
        {
            if (downloadHistory)
            {
                RejectLegacyAssignment(Mocker.Resolve<DownloadHistoryRepository>(), DownloadGrab(), writer);
            }
            else
            {
                RejectLegacyAssignment(Mocker.Resolve<HistoryRepository>(), MovieGrab(), writer);
            }
        }
    }
}
