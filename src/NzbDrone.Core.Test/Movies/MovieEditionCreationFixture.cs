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
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionCreationFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private QualityProfile _availableProfile;

        [SetUp]
        public void Setup()
        {
            var primaryProfile = Db.Insert(new QualityProfile { Name = "Synthetic Primary", Items = new(), Language = Language.English });
            _availableProfile = Db.Insert(new QualityProfile { Name = "Synthetic New Target", Items = new(), Language = Language.English });
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.MovieFileId = 0)
                .With(m => m.QualityProfileId = primaryProfile.Id).With(m => m.PrimaryEditionId = null).BuildNew());
            _primary = Subject.Insert(new MovieEdition
            {
                MovieId = _movie.Id, EditionKey = "synthetic-primary", Name = "Synthetic Theatrical IMAX", QualityProfileId = primaryProfile.Id,
                Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "framing", "imax" } }
            });
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private MovieEdition Candidate() => new()
        {
            MovieId = _movie.Id, Name = "Synthetic New 3D IMAX", EditionKey = "custom-" + Guid.NewGuid().ToString("N"),
            QualityProfileId = _availableProfile.Id, Attributes = new() { { "cut", "theatrical" }, { "stereoscopic", "3d" }, { "framing", "imax" } }
        };

        [Test]
        public void creates_empty_unmonitored_target_without_changing_primary_or_default()
        {
            var created = Subject.CreateUnmonitored(Candidate(), _movie, Subject.GetByMovie(_movie.Id));
            created.Id.Should().BePositive();
            Subject.Get(created.Id).Attributes.Should().BeEquivalentTo(created.Attributes);
            created.Monitored.Should().BeFalse();
            created.MovieFileId.Should().BeNull();
            var parent = Db.Single<Movie>();
            parent.PrimaryEditionId.Should().Be(_primary.Id);
            parent.MovieFileId.Should().Be(0);
            parent.QualityProfileId.Should().Be(_movie.QualityProfileId);
            parent.Monitored.Should().Be(_movie.Monitored);
            parent.Path.Should().Be(_movie.Path);
        }

        [TestCase("target-added")]
        [TestCase("target-removed")]
        [TestCase("name")]
        [TestCase("profile")]
        [TestCase("current-file")]
        [TestCase("attributes")]
        [TestCase("rules")]
        [TestCase("monitoring")]
        [TestCase("parent-profile")]
        [TestCase("primary")]
        [TestCase("path")]
        public void rejects_changed_preview_snapshot_without_inserting(string change)
        {
            var before = Subject.GetByMovie(_movie.Id);
            using var connection = Db.OpenConnection();
            switch (change)
            {
                case "target-added": Db.Insert(Candidate()); break;
                case "target-removed": Db.Delete(_primary); break;
                case "name": _primary.Name += " changed"; Db.Update(_primary); break;
                case "profile": _primary.QualityProfileId = _availableProfile.Id; Db.Update(_primary); break;
                case "current-file": _primary.MovieFileId = 999; Db.Update(_primary); break;
                case "attributes": _primary.Attributes["cut"] = "directors-cut"; Db.Update(_primary); break;
                case "rules": _primary.MatchRules.Add("SyntheticGroup$"); Db.Update(_primary); break;
                case "monitoring": _primary.Monitored = !_primary.Monitored; Db.Update(_primary); break;
                case "parent-profile": connection.Execute(@"UPDATE ""Movies"" SET ""QualityProfileId"" = @ProfileId WHERE ""Id"" = @Id", new { _movie.Id, ProfileId = _availableProfile.Id }); break;
                case "primary": connection.Execute(@"UPDATE ""Movies"" SET ""PrimaryEditionId"" = NULL WHERE ""Id"" = @Id", new { _movie.Id }); break;
                case "path": connection.Execute(@"UPDATE ""Movies"" SET ""Path"" = @Path WHERE ""Id"" = @Id", new { _movie.Id, Path = _movie.Path + "-moved" }); break;
            }

            var count = Subject.Count();
            Action create = () => Subject.CreateUnmonitored(Candidate(), _movie, before);
            create.Should().Throw<InvalidOperationException>().WithMessage("*changed*");
            Subject.Count().Should().Be(count);
        }

        [Test]
        public void missing_profile_or_parent_cannot_leave_a_created_target()
        {
            var before = Subject.GetByMovie(_movie.Id);
            Db.Delete(_availableProfile);
            Action create = () => Subject.CreateUnmonitored(Candidate(), _movie, before);
            create.Should().Throw<ModelNotFoundException>();
            Subject.Count().Should().Be(1);
            Db.Delete(_movie);
            create.Should().Throw<ModelNotFoundException>();
            Subject.Count().Should().Be(1);
        }

        [Test]
        public void invalid_rules_roll_back_the_insert_in_the_validated_creation_path()
        {
            var candidate = Candidate();
            candidate.MatchRules.Add("[");
            Action create = () => Subject.CreateUnmonitored(candidate, _movie, Subject.GetByMovie(_movie.Id));
            create.Should().Throw<ArgumentException>();
            Subject.Count().Should().Be(1);
        }

        [Test]
        public void rejects_duplicate_display_name_with_a_distinct_key()
        {
            var candidate = Candidate();
            candidate.Name = _primary.Name.ToUpperInvariant();
            Action create = () => Subject.CreateUnmonitored(candidate, _movie, Subject.GetByMovie(_movie.Id));
            create.Should().Throw<InvalidOperationException>().WithMessage("*name already exists*");
            Subject.Count().Should().Be(1);
        }

        [Test]
        public void concurrent_creations_from_the_same_snapshot_cannot_both_commit()
        {
            var repository = Subject;
            var before = Subject.GetByMovie(_movie.Id);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    var candidate = Candidate();
                    candidate.Name += index;
                    repository.CreateUnmonitored(candidate, _movie, before);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })).ToArray();
            start.Set();
            Task.WhenAll(tasks).GetAwaiter().GetResult().Count(success => success).Should().Be(1);
            Subject.Count().Should().Be(2);
        }

        [Test]
        public void creation_and_profile_deletion_cannot_commit_a_dangling_profile_reference()
        {
            var repository = Subject;
            var profiles = Mocker.Resolve<QualityProfileRepository>();
            var before = Subject.GetByMovie(_movie.Id);
            using var start = new ManualResetEventSlim();
            var create = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    repository.CreateUnmonitored(Candidate(), _movie, before);
                    return true;
                }
                catch (ModelNotFoundException)
                {
                    return false;
                }
            });
            var delete = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    profiles.Delete(_availableProfile.Id);
                    return true;
                }
                catch (QualityProfileInUseException)
                {
                    return false;
                }
            });
            start.Set();
            Task.WhenAll(create, delete).GetAwaiter().GetResult();
            create.Result.Should().NotBe(delete.Result);
            var exists = Db.All<QualityProfile>().Any(p => p.Id == _availableProfile.Id);
            exists.Should().Be(create.Result);
            Subject.GetByMovie(_movie.Id).Count(e => e.QualityProfileId == _availableProfile.Id).Should().Be(create.Result ? 1 : 0);
        }
    }
}
