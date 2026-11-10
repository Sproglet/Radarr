using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    public class EditionProfileDeletionFixture : DbTest
    {
        private QualityProfileRepository _profiles;
        private MovieEditionRepository _editions;
        private QualityProfile _primaryProfile;
        private QualityProfile _secondaryProfile;
        private QualityProfile _unusedProfile;
        private MovieEdition _secondary;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            _profiles = Mocker.Resolve<QualityProfileRepository>();
            _editions = Mocker.Resolve<MovieEditionRepository>();
            _primaryProfile = _profiles.Insert(new QualityProfile { Name = "Synthetic Primary Profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = Language.English });
            _secondaryProfile = _profiles.Insert(new QualityProfile { Name = "Synthetic Secondary Profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = Language.English });
            _unusedProfile = _profiles.Insert(new QualityProfile { Name = "Synthetic Unused Profile", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = Language.English });
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew().With(m => m.Title = "Synthetic Lunar Voyage").BuildNew());
            var movie = Mocker.Resolve<MovieRepository>().Insert(new Movie
            {
                MovieMetadataId = metadata.Id, MovieMetadata = metadata, QualityProfileId = _primaryProfile.Id,
                Path = "/synthetic/profile-deletion", Monitored = false
            });
            _secondary = _editions.Insert(new MovieEdition
            {
                MovieId = movie.Id, EditionKey = "synthetic-secondary", Name = "Synthetic Directors 3D",
                QualityProfileId = _secondaryProfile.Id, Monitored = false
            });
        }

        [Test]
        public void inactive_empty_secondary_still_prevents_profile_deletion()
        {
            _editions.UsesQualityProfile(_secondaryProfile.Id).Should().BeTrue();
            _editions.UsesQualityProfile(_unusedProfile.Id).Should().BeFalse();
            Action delete = () => _profiles.Delete(_secondaryProfile.Id);
            delete.Should().Throw<QualityProfileInUseException>();
            _profiles.Exists(_secondaryProfile.Id).Should().BeTrue();
            _editions.Get(_secondary.Id).QualityProfileId.Should().Be(_secondaryProfile.Id);
        }

        [Test]
        public void mixed_bulk_delete_rolls_back_unused_and_used_profiles()
        {
            Action delete = () => _profiles.DeleteMany(new[] { _unusedProfile.Id, _secondaryProfile.Id });
            delete.Should().Throw<QualityProfileInUseException>();
            _profiles.Exists(_unusedProfile.Id).Should().BeTrue();
            _profiles.Exists(_secondaryProfile.Id).Should().BeTrue();
            _profiles.Exists(_primaryProfile.Id).Should().BeTrue();
        }

        [Test]
        public void purge_rolls_back_all_profiles_when_an_edition_references_one()
        {
            Action delete = () => _profiles.Purge();
            delete.Should().Throw<QualityProfileInUseException>();
            _profiles.All().Should().HaveCount(3);
        }

        [Test]
        public void profile_deletion_succeeds_after_the_secondary_reference_is_removed()
        {
            _secondary.QualityProfileId = _primaryProfile.Id;
            _editions.SetFields(_secondary, e => e.QualityProfileId);
            _profiles.Delete(_secondaryProfile.Id);
            _profiles.Exists(_secondaryProfile.Id).Should().BeFalse();
            _profiles.Exists(_primaryProfile.Id).Should().BeTrue();
            _editions.Get(_secondary.Id).QualityProfileId.Should().Be(_primaryProfile.Id);
        }

        [Test]
        public void unrelated_invalid_edition_does_not_block_an_unused_profile_delete()
        {
            _secondary.QualityProfileId = 999999;
            _editions.SetFields(_secondary, e => e.QualityProfileId);
            _profiles.Delete(_unusedProfile.Id);
            _profiles.Exists(_unusedProfile.Id).Should().BeFalse();
            _editions.Get(_secondary.Id).QualityProfileId.Should().Be(999999);
        }

        [Test]
        public void deleting_a_missing_profile_is_a_noop()
        {
            _profiles.Delete(999999);
            _profiles.All().Should().HaveCount(3);
        }
    }
}
