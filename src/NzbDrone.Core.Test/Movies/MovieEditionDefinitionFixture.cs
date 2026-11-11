using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionDefinitionFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _target;

        [SetUp]
        public void Setup()
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Definition Profile", Items = new(), Language = Language.English });
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.QualityProfileId = profile.Id).With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null).With(m => m.Monitored = true).BuildNew());
            _primary = Subject.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-imax", Name = "Synthetic Theatrical IMAX", QualityProfileId = profile.Id });
            _target = Subject.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D", QualityProfileId = profile.Id,
                Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" } }, Monitored = true,
                LastSearchTime = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc) });
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private MovieEditionDefinitionChange Change() => new()
        {
            MovieId = _movie.Id, EditionId = _target.Id, Name = "Synthetic New Directors 3D",
            Attributes = new(_target.Attributes), MatchRules = new(_target.MatchRules),
            ExpectedMovieMonitored = _movie.Monitored, ExpectedEditionMonitored = _target.Monitored,
            ExpectedMovieFileId = _target.MovieFileId, ExpectedQualityProfileId = _target.QualityProfileId,
            ExpectedRuleRevision = _target.RuleRevision, ExpectedName = _target.Name
        };

        private MovieEdition Proposed(MovieEditionDefinitionChange change) => new()
        {
            Id = change.EditionId, MovieId = change.MovieId, Name = change.Name, Attributes = change.Attributes, MatchRules = change.MatchRules
        };

        private void Attach()
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = null)
                .With(f => f.RelativePath = "Synthetic-Definition-SyntheticGroup.mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            Subject.SetCurrentFile(_movie.Id, _target.Id, file.Id, null);
            _target = Subject.Get(_target.Id);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void changes_only_definition_and_preserves_identity_assignments_preferences_and_primary(bool primary)
        {
            if (primary) { _target = _primary; }
            Attach();
            var beforeMovie = Db.Single<Movie>();
            var beforeFiles = Db.All<MovieFile>();
            var beforeOther = Subject.GetByMovie(_movie.Id).Single(e => e.Id != _target.Id);
            var request = Change();
            var proposed = Proposed(request);
            proposed.MovieFileId = 999999;
            proposed.EditionKey = "synthetic-malicious-key";
            proposed.Monitored = !_target.Monitored;
            proposed.QualityProfileId = 999999;
            proposed.RuleRevision = 999999;
            var updated = Subject.SetDefinition(request, proposed, _movie, Subject.GetByMovie(_movie.Id));
            updated.Name.Should().Be(request.Name);
            updated.EditionKey.Should().Be(_target.EditionKey);
            updated.MovieFileId.Should().Be(_target.MovieFileId);
            updated.QualityProfileId.Should().Be(_target.QualityProfileId);
            updated.Monitored.Should().Be(_target.Monitored);
            updated.LastSearchTime.Should().Be(_target.LastSearchTime);
            updated.RuleRevision.Should().Be(_target.RuleRevision);
            var afterMovie = Db.Single<Movie>();
            afterMovie.PrimaryEditionId.Should().Be(beforeMovie.PrimaryEditionId);
            afterMovie.MovieFileId.Should().Be(beforeMovie.MovieFileId);
            afterMovie.QualityProfileId.Should().Be(beforeMovie.QualityProfileId);
            afterMovie.Monitored.Should().Be(beforeMovie.Monitored);
            afterMovie.Path.Should().Be(beforeMovie.Path);
            afterMovie.LastSearchTime.Should().Be(beforeMovie.LastSearchTime);
            Db.All<MovieFile>().Should().BeEquivalentTo(beforeFiles);
            Subject.Get(beforeOther.Id).Should().BeEquivalentTo(beforeOther);
        }

        [TestCase("rules")]
        [TestCase("attributes")]
        [TestCase("both")]
        [TestCase("attribute-key-case")]
        [TestCase("unchanged")]
        public void increments_revision_once_only_for_classification_changes(string kind)
        {
            var request = Change();
            if (kind is "rules" or "both") request.MatchRules.Add("SyntheticGroup$");
            if (kind is "attributes" or "both") request.Attributes["branding"] = "Synthetic Collector";
            if (kind == "attribute-key-case") { request.Attributes.Remove("cut"); request.Attributes["CUT"] = "directors-cut"; }
            var saved = Subject.SetDefinition(request, Proposed(request), _movie, Subject.GetByMovie(_movie.Id));
            saved.RuleRevision.Should().Be(_target.RuleRevision + (kind == "unchanged" ? 0 : 1));
            saved.Attributes.Should().BeEquivalentTo(request.Attributes);
            saved.Attributes.Should().Contain("stereoscopic", "3d");
            saved.MatchRules.Should().BeEquivalentTo(request.MatchRules);
        }

        [TestCase("name")]
        [TestCase("revision")]
        [TestCase("profile")]
        [TestCase("file")]
        [TestCase("monitoring")]
        [TestCase("master")]
        [TestCase("sibling")]
        [TestCase("path")]
        [TestCase("primary")]
        [TestCase("parent-profile")]
        [TestCase("target-added")]
        public void rejects_stale_request_or_server_preview_without_changing_stored_rows(string kind)
        {
            Attach();
            var request = Change();
            var expected = Subject.GetByMovie(_movie.Id);
            switch (kind)
            {
                case "name": request.ExpectedName = "Synthetic Stale"; break;
                case "revision": request.ExpectedRuleRevision++; break;
                case "profile": request.ExpectedQualityProfileId++; break;
                case "file": request.ExpectedMovieFileId = null; break;
                case "monitoring": request.ExpectedEditionMonitored = !request.ExpectedEditionMonitored; break;
                case "master": request.ExpectedMovieMonitored = !request.ExpectedMovieMonitored; break;
                case "sibling": _primary.Name += " changed"; Db.Update(_primary); break;
                case "path": using (var conn = Db.OpenConnection()) conn.Execute(@"UPDATE ""Movies"" SET ""Path"" = 'Synthetic Changed' WHERE ""Id"" = @Id", _movie); break;
                case "primary": using (var conn = Db.OpenConnection()) conn.Execute(@"UPDATE ""Movies"" SET ""PrimaryEditionId"" = @Target WHERE ""Id"" = @Id", new { _movie.Id, Target = _target.Id }); break;
                case "parent-profile": using (var conn = Db.OpenConnection()) conn.Execute(@"UPDATE ""Movies"" SET ""QualityProfileId"" = 999999 WHERE ""Id"" = @Id", _movie); break;
                case "target-added": Subject.Insert(new MovieEdition { MovieId = _movie.Id, QualityProfileId = _movie.QualityProfileId, Name = "Synthetic Added", EditionKey = "synthetic-added" }); break;
            }

            var before = Subject.All();
            Action save = () => Subject.SetDefinition(request, Proposed(request), _movie, expected);
            save.Should().Throw<InvalidOperationException>();
            Subject.All().Should().BeEquivalentTo(before);
        }

        [TestCase("duplicate")]
        [TestCase("invalid-rules")]
        [TestCase("control-name")]
        [TestCase("missing-profile")]
        [TestCase("broken-file")]
        [TestCase("revision-overflow")]
        public void rejects_invalid_definition_or_damaged_context_atomically(string kind)
        {
            if (kind == "missing-profile") { using var conn = Db.OpenConnection(); conn.Execute(@"UPDATE ""MovieEditions"" SET ""QualityProfileId"" = 999999 WHERE ""Id"" = @Id", _target); }
            if (kind == "broken-file") { using var conn = Db.OpenConnection(); conn.Execute(@"UPDATE ""MovieEditions"" SET ""MovieFileId"" = 999999 WHERE ""Id"" = @Id", _target); }
            if (kind == "revision-overflow") { using var conn = Db.OpenConnection(); conn.Execute(@"UPDATE ""MovieEditions"" SET ""RuleRevision"" = @Revision WHERE ""Id"" = @Id", new { _target.Id, Revision = int.MaxValue }); }
            _target = Subject.Get(_target.Id);
            var request = Change();
            if (kind == "duplicate") request.Name = " " + _primary.Name.ToUpperInvariant() + " ";
            if (kind == "invalid-rules") request.MatchRules.Add("[");
            if (kind == "control-name") request.Name = "Synthetic\0Cut";
            if (kind == "revision-overflow") request.MatchRules.Add("SyntheticGroup$");
            var before = Subject.All();
            Action save = () => Subject.SetDefinition(request, Proposed(request), _movie, Subject.GetByMovie(_movie.Id));
            if (kind is "invalid-rules" or "control-name") save.Should().Throw<ArgumentException>();
            else if (kind == "missing-profile") save.Should().Throw<ModelNotFoundException>();
            else if (kind == "revision-overflow") save.Should().Throw<OverflowException>();
            else save.Should().Throw<InvalidOperationException>();
            Subject.All().Should().BeEquivalentTo(before);
        }

        [Test]
        public void serializes_competing_definition_edits_from_the_same_snapshot()
        {
            var expected = Subject.GetByMovie(_movie.Id);
            var first = Change();
            first.MatchRules.Add("SyntheticFirst$");
            var second = Change();
            second.MatchRules.Add("SyntheticSecond$");
            var success = 0;
            var conflict = 0;
            using var barrier = new Barrier(2);
            Task Edit(MovieEditionDefinitionChange request) => Task.Run(() =>
            {
                barrier.SignalAndWait();
                try { Subject.SetDefinition(request, Proposed(request), _movie, expected); Interlocked.Increment(ref success); }
                catch (InvalidOperationException) { Interlocked.Increment(ref conflict); }
            });
            Task.WaitAll(Edit(first), Edit(second));
            success.Should().Be(1);
            conflict.Should().Be(1);
            Subject.Get(_target.Id).RuleRevision.Should().Be(_target.RuleRevision + 1);
        }
    }
}
