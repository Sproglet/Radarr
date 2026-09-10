using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Movies
{
    [TestFixture]
    public class MovieEditionRepositoryFixture : DbTest<MovieEditionRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null)
                .BuildNew();
            Db.Insert(_movie);
            _primary = AddEdition("theatrical");
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        [Test]
        public void should_assign_file_and_update_primary_projection()
        {
            var file = AddFile();

            Subject.SetCurrentFile(_movie.Id, _primary.Id, file.Id, null);

            Subject.Get(_primary.Id).MovieFileId.Should().Be(file.Id);
            Db.All<MovieFile>().Should().ContainSingle().Which.MovieEditionId.Should().Be(_primary.Id);
            Db.All<Movie>().Should().ContainSingle().Which.MovieFileId.Should().Be(file.Id);
        }

        [Test]
        public void should_not_change_primary_projection_for_secondary_edition()
        {
            var secondary = AddEdition("theatrical-3d-imax");
            var file = AddFile();

            Subject.SetCurrentFile(_movie.Id, secondary.Id, file.Id, null);

            Subject.Get(secondary.Id).MovieFileId.Should().Be(file.Id);
            Subject.Get(_primary.Id).MovieFileId.Should().BeNull();
            Db.All<Movie>().Should().ContainSingle().Which.MovieFileId.Should().Be(0);
        }

        [Test]
        public void should_rollback_file_claim_when_current_file_changed()
        {
            var original = AddFile();
            var replacement = AddFile();
            Subject.SetCurrentFile(_movie.Id, _primary.Id, original.Id, null);

            Action act = () => Subject.SetCurrentFile(_movie.Id, _primary.Id, replacement.Id, null);

            act.Should().Throw<InvalidOperationException>();
            Subject.Get(_primary.Id).MovieFileId.Should().Be(original.Id);
            Db.All<MovieFile>().Should().Contain(f => f.Id == replacement.Id && f.MovieEditionId == null);
        }

        [Test]
        public void should_reject_file_owned_by_another_edition()
        {
            var secondary = AddEdition("extended");
            var file = AddFile();
            Subject.SetCurrentFile(_movie.Id, secondary.Id, file.Id, null);

            Action act = () => Subject.SetCurrentFile(_movie.Id, _primary.Id, file.Id, null);

            act.Should().Throw<InvalidOperationException>();
            Subject.Get(secondary.Id).MovieFileId.Should().Be(file.Id);
            Subject.Get(_primary.Id).MovieFileId.Should().BeNull();
        }

        [Test]
        public void should_reject_file_from_another_movie()
        {
            var file = AddFile();
            file.MovieId = _movie.Id + 100;
            Db.Update(file);

            Action act = () => Subject.SetCurrentFile(_movie.Id, _primary.Id, file.Id, null);

            act.Should().Throw<InvalidOperationException>();
            Subject.Get(_primary.Id).MovieFileId.Should().BeNull();
        }

        [Test]
        public void should_replace_only_the_expected_current_file()
        {
            var original = AddFile();
            var replacement = AddFile();
            Subject.SetCurrentFile(_movie.Id, _primary.Id, original.Id, null);

            Subject.SetCurrentFile(_movie.Id, _primary.Id, replacement.Id, original.Id);

            Subject.Get(_primary.Id).MovieFileId.Should().Be(replacement.Id);
            Db.All<Movie>().Should().ContainSingle().Which.MovieFileId.Should().Be(replacement.Id);
            Db.All<MovieFile>().Should().HaveCount(2);
        }

        [Test]
        public void should_reject_target_edition_from_another_movie()
        {
            var secondary = AddEdition("extended");
            secondary.MovieId = _movie.Id + 100;
            Db.Update(secondary);
            var file = AddFile();

            Action act = () => Subject.SetCurrentFile(_movie.Id, secondary.Id, file.Id, null);

            act.Should().Throw<InvalidOperationException>();
            Db.All<MovieFile>().Should().ContainSingle().Which.MovieEditionId.Should().BeNull();
        }

        [Test]
        public void should_round_trip_combined_attributes_and_custom_rules()
        {
            _primary.Attributes = new Dictionary<string, string>
            {
                { "cut", "theatrical" },
                { "stereoscopic", "3d" },
                { "framing", "imax" },
                { "custom-attribute", "custom-value" }
            };
            _primary.MatchRules.Add(@"\bExample[ .]Edition\b");
            Db.Update(_primary);

            var stored = Subject.Get(_primary.Id);
            stored.Attributes.Should().BeEquivalentTo(_primary.Attributes);
            stored.MatchRules.Should().BeEquivalentTo(_primary.MatchRules);
        }

        private MovieEdition AddEdition(string key)
        {
            var edition = new MovieEdition
            {
                MovieId = _movie.Id,
                EditionKey = key,
                Name = key,
                QualityProfileId = _movie.QualityProfileId,
                Monitored = true
            };
            Db.Insert(edition);
            return edition;
        }

        private MovieFile AddFile()
        {
            var file = Builder<MovieFile>.CreateNew()
                .With(f => f.MovieId = _movie.Id)
                .With(f => f.MovieEditionId = null)
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English })
                .BuildNew();
            Db.Insert(file);
            return file;
        }
    }
}
