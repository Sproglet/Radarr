using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Collections;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class QualityProfileReferenceFixture : DbTest
    {
        private QualityProfileRepository _profiles;
        private MovieRepository _movies;
        private MovieEditionRepository _editions;
        private MovieCollectionRepository _collections;
        private ImportListRepository _lists;
        private QualityProfile _baseProfile;
        private QualityProfile _available;
        private Movie _movie;
        private MovieEdition _edition;
        private MovieCollection _collection;
        private ImportListDefinition _list;
        private int _number;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            _profiles = Mocker.Resolve<QualityProfileRepository>();
            _movies = Mocker.Resolve<MovieRepository>();
            _editions = Mocker.Resolve<MovieEditionRepository>();
            _collections = Mocker.Resolve<MovieCollectionRepository>();
            _lists = Mocker.Resolve<ImportListRepository>();
            _baseProfile = _profiles.Insert(new QualityProfile { Name = "Synthetic Reference Base", Items = new(), Language = Language.English });
            _available = _profiles.Insert(new QualityProfile { Name = "Synthetic Reference Available", Items = new(), Language = Language.English });
            _movie = _movies.Insert(Movie(_baseProfile.Id));
            _edition = _editions.Insert(Edition(_baseProfile.Id));
            _collection = _collections.Insert(Collection(_baseProfile.Id));
            _list = _lists.Insert(List(_baseProfile.Id));
        }

        private Movie Movie(int profile) => new() { MovieMetadataId = ++_number, QualityProfileId = profile, Path = "/synthetic/reference-" + _number, Added = DateTime.UtcNow };
        private MovieEdition Edition(int profile) => new() { MovieId = _movie.Id, EditionKey = "synthetic-reference-" + ++_number, Name = "Synthetic Collector " + _number, QualityProfileId = profile };
        private MovieCollection Collection(int profile) => new() { TmdbId = -1000000 - ++_number, Title = "Synthetic Collection " + _number, CleanTitle = "syntheticcollection" + _number,
            SortTitle = "syntheticcollection" + _number, Overview = "", QualityProfileId = profile, RootFolderPath = "/synthetic", Added = DateTime.UtcNow };
        private ImportListDefinition List(int profile) => new() { Name = "Synthetic List " + ++_number, Implementation = "SyntheticImport", Settings = NullConfig.Instance,
            ConfigContract = "SyntheticUnknownSettings", QualityProfileId = profile, RootFolderPath = "/synthetic" };

        private void Insert(string owner, int profile)
        {
            switch (owner)
            {
                case "movie": _movies.Insert(Movie(profile)); break;
                case "edition": _editions.Insert(Edition(profile)); break;
                case "collection": _collections.Insert(Collection(profile)); break;
                case "list": _lists.Insert(List(profile)); break;
            }
        }

        private void Assign(string owner, int profile, string mode)
        {
            switch (owner)
            {
                case "movie": Apply(_movies, _movie, () => _movie.QualityProfileId = profile, m => m.QualityProfileId, mode); break;
                case "edition": Apply(_editions, _edition, () => _edition.QualityProfileId = profile, e => e.QualityProfileId, mode); break;
                case "collection": Apply(_collections, _collection, () => _collection.QualityProfileId = profile, c => c.QualityProfileId, mode); break;
                case "list": Apply(_lists, _list, () => _list.QualityProfileId = profile, l => l.QualityProfileId, mode); break;
            }
        }

        private static void Apply<T>(BasicRepository<T> repository, T row, Action set, Expression<Func<T, object>> field, string mode)
            where T : ModelBase, new()
        {
            set();
            switch (mode)
            {
                case "update": repository.Update(row); break;
                case "partial": repository.SetFields(row, field); break;
                case "bulk": repository.UpdateMany(new[] { row, new T { Id = row.Id + 1000 } }); break;
                case "bulk-partial": repository.SetFields(new[] { row, new T { Id = row.Id + 1000 } }, field); break;
            }
        }

        private int References(int profile)
        {
            using var connection = Db.OpenConnection();
            return new[] { "Movies", "MovieEditions", "Collections", "ImportLists" }.Sum(table =>
                connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE \"QualityProfileId\"=@Profile", new { Profile = profile }));
        }

        [TestCase("movie")]
        [TestCase("edition")]
        [TestCase("collection")]
        [TestCase("list")]
        public void mixed_bulk_inserts_roll_back_all_rows_when_a_profile_is_missing(string owner)
        {
            var before = References(_baseProfile.Id);
            Action insert = () =>
            {
                switch (owner)
                {
                    case "movie": _movies.InsertMany(new[] { Movie(_baseProfile.Id), Movie(999999) }); break;
                    case "edition": _editions.InsertMany(new[] { Edition(_baseProfile.Id), Edition(999999) }); break;
                    case "collection": _collections.InsertMany(new[] { Collection(_baseProfile.Id), Collection(999999) }); break;
                    case "list": _lists.InsertMany(new[] { List(_baseProfile.Id), List(999999) }); break;
                }
            };
            insert.Should().Throw<ModelNotFoundException>();
            References(_baseProfile.Id).Should().Be(before);
            References(999999).Should().Be(0);
        }

        [TestCase("movie", true)]
        [TestCase("edition", true)]
        [TestCase("collection", true)]
        [TestCase("list", true)]
        [TestCase("movie", false)]
        [TestCase("edition", false)]
        [TestCase("collection", false)]
        [TestCase("list", false)]
        public void missing_profiles_cannot_be_newly_referenced(string owner, bool insert)
        {
            var before = References(_baseProfile.Id);
            Action assign = () => { if (insert) Insert(owner, 999999); else Assign(owner, 999999, "partial"); };
            assign.Should().Throw<ModelNotFoundException>();
            References(999999).Should().Be(0);
            References(_baseProfile.Id).Should().Be(before);
        }

        [TestCase("movie", "bulk")]
        [TestCase("edition", "bulk")]
        [TestCase("collection", "bulk")]
        [TestCase("list", "bulk")]
        [TestCase("movie", "bulk-partial")]
        [TestCase("edition", "bulk-partial")]
        [TestCase("collection", "bulk-partial")]
        [TestCase("list", "bulk-partial")]
        public void bulk_writes_rollback_earlier_profile_assignments_when_later_rows_fail(string owner, string mode)
        {
            var before = References(_baseProfile.Id);
            Action assign = () => Assign(owner, _available.Id, mode);
            assign.Should().Throw<InvalidOperationException>();
            References(_available.Id).Should().Be(0);
            References(_baseProfile.Id).Should().Be(before);
        }

        [TestCase("movie")]
        [TestCase("edition")]
        [TestCase("collection")]
        [TestCase("list")]
        public void unrelated_full_updates_preserve_preexisting_bad_references(string owner)
        {
            // Damage is fixture data, deliberately bypassing production reference writers.
            switch (owner)
            {
                case "movie": _movie.QualityProfileId = 999999; Db.Update(_movie); _movie.Path += "-metadata"; break;
                case "edition": _edition.QualityProfileId = 999999; Db.Update(_edition); _edition.Name += " metadata"; break;
                case "collection": _collection.QualityProfileId = 999999; Db.Update(_collection); _collection.Title += " metadata"; break;
                case "list": _list.QualityProfileId = 999999; Db.Update(_list); _list.Name += " metadata"; break;
            }
            Assign(owner, 999999, "update");
            References(999999).Should().Be(1);
            _profiles.Delete(_available.Id);
            _profiles.Exists(_available.Id).Should().BeFalse();
        }

        [TestCase("movie", true)]
        [TestCase("edition", true)]
        [TestCase("collection", true)]
        [TestCase("list", true)]
        [TestCase("movie", false)]
        [TestCase("edition", false)]
        [TestCase("collection", false)]
        [TestCase("list", false)]
        public void assignment_and_deletion_race_cannot_commit_a_dangling_reference(string owner, bool insert)
        {
            using var start = new ManualResetEventSlim();
            var assignment = Task.Run(() =>
            {
                start.Wait();
                try { if (insert) Insert(owner, _available.Id); else Assign(owner, _available.Id, "partial"); return true; }
                catch (ModelNotFoundException) { return false; }
            });
            var deletion = Task.Run(() =>
            {
                start.Wait();
                try { _profiles.Delete(_available.Id); return true; }
                catch (QualityProfileInUseException) { return false; }
            });
            start.Set();
            Task.WhenAll(assignment, deletion).GetAwaiter().GetResult();
            (assignment.Result ^ deletion.Result).Should().BeTrue();
            if (assignment.Result)
            {
                _profiles.Exists(_available.Id).Should().BeTrue();
                References(_available.Id).Should().BePositive();
            }
            else
            {
                _profiles.Exists(_available.Id).Should().BeFalse();
                References(_available.Id).Should().Be(0);
            }
        }
    }
}
