using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieEditionLifecycleFixture : DbTest<MovieRepository, Movie>
    {
        private int _metadataId;
        private int _profileId;
        private int _otherProfileId;

        [SetUp]
        public void Setup()
        {
            _profileId = Db.Insert(new QualityProfile { Name = "Synthetic Lifecycle Default", Items = new(), Language = Language.English }).Id;
            _otherProfileId = Db.Insert(new QualityProfile { Name = "Synthetic Lifecycle Other", Items = new(), Language = Language.English }).Id;
        }

        private Movie NewMovie()
        {
            return Builder<Movie>.CreateNew()
                .With(m => m.Title = "Synthetic Lunar Voyage")
                .With(m => m.MovieMetadataId = ++_metadataId)
                .With(m => m.QualityProfileId = _profileId)
                .With(m => m.MovieFileId = 0)
                .With(m => m.PrimaryEditionId = null)
                .With(m => m.LastSearchTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))
                .BuildNew();
        }

        private MovieFile FileFor(Movie movie)
        {
            return Db.Insert(Builder<MovieFile>.CreateNew()
                .With(f => f.MovieId = movie.Id)
                .With(f => f.MovieEditionId = null)
                .With(f => f.RelativePath = "Synthetic.Lunar.Voyage.2026.IMAX.3D-SyntheticGroup.mkv")
                .With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English })
                .BuildNew());
        }

        private MovieEdition Secondary(Movie movie)
        {
            return Db.Insert(new MovieEdition
            {
                MovieId = movie.Id,
                EditionKey = "custom-3d-imax",
                Name = "Custom 3D IMAX",
                QualityProfileId = 99,
                Attributes = new Dictionary<string, string> { { "cut", "theatrical" }, { "stereoscopic", "3d" }, { "framing", "imax" }, { "branding", "synthetic" } }
            });
        }

        [Test]
        public void should_read_only_primary_file_through_legacy_movie_queries()
        {
            var profile = Db.Insert(new QualityProfile
            {
                Name = "Synthetic Profile",
                Items = new List<QualityProfileQualityItem>(),
                Language = Language.English
            });
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew()
                .With(m => m.Title = "Synthetic Lunar Voyage")
                .With(m => m.CleanTitle = "syntheticlunarvoyage")
                .BuildNew());
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            Mocker.SetConstant<IAlternativeTitleRepository>(Mocker.Resolve<AlternativeTitleRepository>());
            var movie = NewMovie();
            movie.QualityProfileId = profile.Id;
            movie.MovieMetadataId = metadata.Id;
            Subject.Insert(movie);
            var secondary = Secondary(movie);
            var secondaryFile = FileFor(movie);
            var primaryFile = FileFor(movie);
            var editions = Mocker.Resolve<MovieEditionRepository>();
            editions.SetCurrentFile(movie.Id, secondary.Id, secondaryFile.Id, null);
            editions.SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, primaryFile.Id, null);

            foreach (var loaded in new[] { Subject.Get(movie.Id), Subject.FindByPath(movie.Path), Subject.FindByTitles(new List<string> { metadata.CleanTitle }).Single(), Subject.All().Single() })
            {
                loaded.MovieFileId.Should().Be(primaryFile.Id);
                loaded.MovieFile.Id.Should().Be(primaryFile.Id);
                loaded.HasFile.Should().BeTrue();
            }

            Mocker.Resolve<MediaFileRepository>().Delete(primaryFile.Id);
            Subject.Get(movie.Id).MovieFile.Should().BeNull();
            Subject.Get(movie.Id).HasFile.Should().BeFalse();
        }

        [Test]
        public void should_commit_legacy_file_insert_with_ownership_and_projection()
        {
            var movie = Subject.Insert(NewMovie());
            var file = FileFor(movie);
            Db.Delete(file);
            file.Id = 0;
            file.Edition = "IMAX 3D Custom Label";
            var repository = Mocker.Resolve<MediaFileRepository>();
            repository.Insert(file);

            Db.Single<Movie>().MovieFileId.Should().Be(file.Id);
            Db.Single<MovieEdition>().MovieFileId.Should().Be(file.Id);
            Db.Single<MovieFile>().MovieEditionId.Should().Be(movie.PrimaryEditionId);
            Db.Single<MovieFile>().Edition.Should().Be("IMAX 3D Custom Label");

            // A stale metadata edit cannot discard ownership.
            file.MovieEditionId = null;
            file.ReleaseGroup = "SyntheticGroup";
            repository.Update(file);
            Db.Single<MovieFile>().MovieEditionId.Should().Be(movie.PrimaryEditionId);
        }

        [Test]
        public void should_rollback_legacy_insert_targeting_secondary_edition()
        {
            var movie = Subject.Insert(NewMovie());
            var secondary = Secondary(movie);
            var file = FileFor(movie);
            Db.Delete(file);
            file.Id = 0;
            file.MovieEditionId = secondary.Id;

            Action insert = () => Mocker.Resolve<MediaFileRepository>().Insert(file);
            insert.Should().Throw<InvalidOperationException>();
            Db.All<MovieFile>().Should().BeEmpty();
            Db.Single<Movie>().MovieFileId.Should().Be(0);
            Db.All<MovieEdition>().Should().OnlyContain(e => e.MovieFileId == null);
        }

        [Test]
        public void should_rollback_primary_settings_when_bulk_movie_update_fails()
        {
            var movie = Subject.Insert(NewMovie());
            var originalProfile = movie.QualityProfileId;
            movie.QualityProfileId = _otherProfileId;
            var missing = NewMovie();
            missing.Id = movie.Id + 100;

            Action update = () => Subject.UpdateMany(new List<Movie> { movie, missing });
            update.Should().Throw<InvalidOperationException>();
            Db.Single<Movie>().QualityProfileId.Should().Be(originalProfile);
            Db.Single<MovieEdition>().QualityProfileId.Should().Be(originalProfile);
        }

        [Test]
        public void should_create_defaults_for_single_and_bulk_adds()
        {
            var single = Subject.Insert(NewMovie());
            var bulk = new List<Movie> { NewMovie(), NewMovie() };
            Subject.InsertMany(bulk);

            foreach (var movie in bulk.Append(single))
            {
                var edition = Db.All<MovieEdition>().Single(e => e.MovieId == movie.Id);
                movie.PrimaryEditionId.Should().Be(edition.Id);
                edition.Name.Should().Be("Theatrical");
                edition.Monitored.Should().Be(movie.Monitored);
                edition.QualityProfileId.Should().Be(movie.QualityProfileId);
                edition.LastSearchTime.Should().Be(movie.LastSearchTime);
                edition.Attributes.Should().BeEmpty();
            }
        }

        [Test]
        public void should_rollback_default_editions_when_bulk_add_fails()
        {
            var first = NewMovie();
            var duplicate = NewMovie();
            duplicate.MovieMetadataId = first.MovieMetadataId;

            Action insert = () => Subject.InsertMany(new List<Movie> { first, duplicate });
            insert.Should().Throw<DbException>();
            Db.All<Movie>().Should().BeEmpty();
            Db.All<MovieEdition>().Should().BeEmpty();
        }

        [Test]
        public void should_preserve_primary_when_deleting_secondary_or_replaced_file()
        {
            var movie = Subject.Insert(NewMovie());
            var secondary = Secondary(movie);
            var oldFile = FileFor(movie);
            var current = FileFor(movie);
            var secondaryFile = FileFor(movie);
            var editions = Mocker.Resolve<MovieEditionRepository>();
            editions.SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, oldFile.Id, null);
            editions.SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, current.Id, oldFile.Id);
            editions.SetCurrentFile(movie.Id, secondary.Id, secondaryFile.Id, null);

            Mocker.Resolve<MediaFileRepository>().DeleteMany(new[] { oldFile.Id, secondaryFile.Id });

            Db.Single<Movie>().MovieFileId.Should().Be(current.Id);
            Db.All<MovieEdition>().Single(e => e.Id == movie.PrimaryEditionId).MovieFileId.Should().Be(current.Id);
            Db.All<MovieEdition>().Single(e => e.Id == secondary.Id).MovieFileId.Should().BeNull();
        }

        [Test]
        public void should_sync_legacy_settings_only_to_primary_and_preserve_file_projection_on_stale_edits()
        {
            var movie = Subject.Insert(NewMovie());
            var secondary = Secondary(movie);
            var file = FileFor(movie);
            Mocker.Resolve<MovieEditionRepository>().SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, file.Id, null);
            movie.QualityProfileId = _otherProfileId;
            movie.LastSearchTime = movie.LastSearchTime.Value.AddDays(1);
            Subject.UpdateMany(new List<Movie> { movie });

            var primary = Db.All<MovieEdition>().Single(e => e.Id == movie.PrimaryEditionId);
            primary.QualityProfileId.Should().Be(_otherProfileId);
            primary.LastSearchTime.Should().Be(movie.LastSearchTime);
            Db.Single<Movie>().MovieFileId.Should().Be(file.Id);
            Db.All<MovieEdition>().Single(e => e.Id == secondary.Id).QualityProfileId.Should().Be(99);

            movie.LastSearchTime = movie.LastSearchTime.Value.AddDays(1);
            Subject.SetFields(movie, m => m.LastSearchTime);
            Db.All<MovieEdition>().Single(e => e.Id == primary.Id).LastSearchTime.Should().Be(movie.LastSearchTime);
        }

        [Test]
        public void should_switch_primary_and_reject_foreign_or_broken_targets()
        {
            var movie = Subject.Insert(NewMovie());
            var secondary = Secondary(movie);
            var file = FileFor(movie);
            var editions = Mocker.Resolve<MovieEditionRepository>();
            editions.SetCurrentFile(movie.Id, secondary.Id, file.Id, null);
            editions.SetPrimaryEdition(movie.Id, secondary.Id);
            Db.Single<Movie>().PrimaryEditionId.Should().Be(secondary.Id);
            Db.Single<Movie>().MovieFileId.Should().Be(file.Id);

            Action wrongMovie = () => editions.SetPrimaryEdition(movie.Id + 100, secondary.Id);
            wrongMovie.Should().Throw<InvalidOperationException>();
            file.MovieEditionId = null;
            Db.Update(file);
            Action broken = () => editions.SetPrimaryEdition(movie.Id, secondary.Id);
            broken.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void should_clear_only_deleted_current_file_and_keep_secondary_files()
        {
            var movie = Subject.Insert(NewMovie());
            var secondary = Secondary(movie);
            var primaryFile = FileFor(movie);
            var secondaryFile = FileFor(movie);
            var editions = Mocker.Resolve<MovieEditionRepository>();
            editions.SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, primaryFile.Id, null);
            editions.SetCurrentFile(movie.Id, secondary.Id, secondaryFile.Id, null);

            Mocker.Resolve<MediaFileRepository>().Delete(primaryFile.Id);

            Db.Single<Movie>().MovieFileId.Should().Be(0);
            Db.All<MovieEdition>().Single(e => e.Id == movie.PrimaryEditionId).MovieFileId.Should().BeNull();
            Db.All<MovieEdition>().Single(e => e.Id == secondary.Id).MovieFileId.Should().Be(secondaryFile.Id);
            Db.All<MovieFile>().Should().ContainSingle().Which.Id.Should().Be(secondaryFile.Id);
        }

        [Test]
        public void should_remove_editions_on_movie_deletion_and_release_retained_file_ownership()
        {
            var movie = Subject.Insert(NewMovie());
            var file = FileFor(movie);
            Mocker.Resolve<MovieEditionRepository>().SetCurrentFile(movie.Id, movie.PrimaryEditionId.Value, file.Id, null);
            Subject.DeleteMany(new[] { movie.Id });

            Db.All<MovieEdition>().Should().BeEmpty();
            Db.All<MovieFile>().Should().ContainSingle().Which.MovieEditionId.Should().BeNull();
        }

        [Test]
        public void should_repair_dangling_and_cross_movie_links_idempotently_without_deleting_files()
        {
            var movie = Subject.Insert(NewMovie());
            var other = Subject.Insert(NewMovie());
            var file = FileFor(movie);
            file.MovieEditionId = other.PrimaryEditionId;
            Db.Update(file);
            var primary = Db.All<MovieEdition>().Single(e => e.Id == movie.PrimaryEditionId);
            primary.MovieFileId = file.Id;
            Db.Update(primary);
            movie.PrimaryEditionId = other.PrimaryEditionId;
            movie.MovieFileId = file.Id;
            Db.Update(movie);

            var cleaner = Mocker.Resolve<CleanupOrphanedMovieMovieFileIds>();
            cleaner.Clean();
            cleaner.Clean();

            Db.All<MovieEdition>().Should().HaveCount(2);
            Db.All<Movie>().Single(m => m.Id == movie.Id).PrimaryEditionId.Should().Be(primary.Id);
            Db.All<Movie>().Single(m => m.Id == movie.Id).MovieFileId.Should().Be(0);
            Db.Single<MovieFile>().MovieEditionId.Should().BeNull();
        }
    }
}
