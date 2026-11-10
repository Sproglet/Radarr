using System;
using System.Collections.Generic;
using System.Linq;
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
    public class MovieEditionWantedRepositoryFixture : DbTest<MovieEditionWantedRepository, MovieEdition>
    {
        private Movie _movie;
        private MovieEdition _primary;
        private MovieEdition _secondary;

        [SetUp]
        public void Setup()
        {
            var first = Db.Insert(new QualityProfile { Name = "Synthetic Default", Items = new(), Language = Language.English });
            var second = Db.Insert(new QualityProfile { Name = "Synthetic Secondary", Items = new(), Language = Language.English });
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew().With(m => m.Title = "Synthetic Lunar Voyage")
                .With(m => m.SortTitle = "syntheticlunarvoyage").With(m => m.Year = 2026).BuildNew());
            _movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.MovieMetadataId = metadata.Id).With(m => m.QualityProfileId = first.Id)
                .With(m => m.MovieFileId = 0).With(m => m.PrimaryEditionId = null).With(m => m.Monitored = true).BuildNew());
            _primary = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-theatrical-imax", Name = "Synthetic Theatrical IMAX",
                QualityProfileId = first.Id, Monitored = true, Attributes = new() { { "cut", "theatrical" }, { "framing", "imax" } } });
            _secondary = Db.Insert(new MovieEdition { MovieId = _movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D",
                QualityProfileId = second.Id, Monitored = true, Attributes = new() { { "cut", "directors-cut" }, { "stereoscopic", "3d" }, { "branding", "Synthetic Collector" } } });
            _movie.PrimaryEditionId = _primary.Id;
            Db.Update(_movie);
        }

        private PagingSpec<MovieEditionWanted> Page(int page = 1, int size = 10, string sort = "title") =>
            new() { Page = page, PageSize = size, SortKey = sort, SortDirection = SortDirection.Ascending };

        private MovieFile Attach(MovieEdition edition)
        {
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = _movie.Id).With(f => f.MovieEditionId = edition.Id)
                .With(f => f.RelativePath = "Synthetic-" + edition.Id + "-SyntheticGroup.mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.DVD)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            edition.MovieFileId = file.Id;
            Db.Update(edition);
            return file;
        }

        private List<QualitiesBelowCutoff> Profiles() => new()
        {
            new(_primary.QualityProfileId, new[] { Quality.DVD.Id }), new(_secondary.QualityProfileId, new[] { Quality.DVD.Id })
        };

        [Test]
        public void missing_returns_each_target_with_parent_profile_and_combined_attributes()
        {
            var page = Subject.Missing(Page(), true);
            page.TotalRecords.Should().Be(2);
            page.Records.Select(e => e.Id).Should().Equal(_primary.Id, _secondary.Id);
            page.Records.Should().OnlyContain(e => e.MovieId == _movie.Id && e.MovieTitle == "Synthetic Lunar Voyage" && e.Year == 2026);
            page.Records.Last().Attributes.Should().BeEquivalentTo(_secondary.Attributes);
            page.Records.Last().QualityProfileId.Should().Be(_secondary.QualityProfileId);
            page.Records.Last().QualityProfileName.Should().Be("Synthetic Secondary");
            Attach(_primary);
            Subject.Missing(Page(), true).Records.Should().ContainSingle().Which.Id.Should().Be(_secondary.Id);
        }

        [Test]
        public void cutoff_uses_only_each_targets_profile_and_current_owned_file()
        {
            var first = Attach(_primary);
            var second = Attach(_secondary);
            var profiles = Profiles();
            profiles[1].QualityIds = new[] { Quality.SDTV.Id };
            Subject.Cutoff(Page(), true, profiles).Records.Should().ContainSingle().Which.ResolvedMovieFileId.Should().Be(first.Id);
            profiles[0].QualityIds = Array.Empty<int>();
            profiles[1].QualityIds = new[] { Quality.DVD.Id };
            Subject.Cutoff(Page(), true, profiles).Records.Should().ContainSingle().Which.ResolvedMovieFileId.Should().Be(second.Id);
            Subject.Cutoff(Page(), true, new()).TotalRecords.Should().Be(0);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void monitoring_filters_effective_monitoring_and_its_complement(bool parent, bool target)
        {
            _movie.Monitored = parent;
            Db.Update(_movie);
            _primary.Monitored = target;
            _secondary.Monitored = false;
            Db.Update(_primary);
            Db.Update(_secondary);
            Subject.Missing(Page(), true).TotalRecords.Should().Be(parent && target ? 1 : 0);
            var disabled = Subject.Missing(Page(), false);
            disabled.TotalRecords.Should().Be(parent && target ? 1 : 2);
            disabled.Records.Should().OnlyContain(e => !(e.MovieMonitored && e.Monitored));
            Attach(_primary);
            Subject.Cutoff(Page(), true, Profiles()).TotalRecords.Should().Be(parent && target ? 1 : 0);
            Subject.Cutoff(Page(), false, Profiles()).TotalRecords.Should().Be(parent && target ? 0 : 1);
        }

        [TestCase("missing")]
        [TestCase("foreign-movie")]
        [TestCase("foreign-edition")]
        [TestCase("unassigned")]
        public void broken_current_links_are_diagnostic_missing_rows_and_never_cutoff_candidates(string broken)
        {
            Attach(_primary);
            var file = Attach(_secondary);
            if (broken == "missing")
            {
                Db.Delete(file);
            }
            else
            {
                if (broken == "foreign-movie") file.MovieId = _movie.Id + 100;
                if (broken == "foreign-edition") file.MovieEditionId = _primary.Id;
                if (broken == "unassigned") file.MovieEditionId = null;
                Db.Update(file);
            }

            var row = Subject.Missing(Page(), true).Records.Single();
            row.Id.Should().Be(_secondary.Id);
            row.MovieFileId.Should().Be(file.Id);
            row.ResolvedMovieFileId.Should().BeNull();
            row.RelativePath.Should().BeNull();
            row.Quality.Should().BeNull();
            Subject.Cutoff(Page(), true, Profiles()).Records.Should().ContainSingle().Which.Id.Should().Be(_primary.Id);
        }

        [Test]
        public void missing_profiles_remain_diagnostic_and_never_borrow_the_default_for_cutoff()
        {
            _secondary.QualityProfileId = 999999;
            Db.Update(_secondary);
            Subject.Missing(Page(), true).Records.Single(e => e.Id == _secondary.Id).ProfileExists.Should().BeFalse();
            Attach(_secondary);
            Subject.Cutoff(Page(), true, Profiles()).TotalRecords.Should().Be(0);
        }

        [Test]
        public void paging_has_stable_edition_ties_and_edition_search_dates_with_nulls_last()
        {
            var one = Subject.Missing(Page(1, 1), true);
            one.TotalRecords.Should().Be(2);
            one.Records.Single().Id.Should().Be(_primary.Id);
            Subject.Missing(Page(2, 1), true).Records.Single().Id.Should().Be(_secondary.Id);
            var beyond = Subject.Missing(Page(3, 1), true);
            beyond.TotalRecords.Should().Be(2);
            beyond.Records.Should().BeEmpty();
            _secondary.LastSearchTime = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
            Db.Update(_secondary);
            Subject.Missing(Page(sort: "lastSearchTime"), true).Records.Select(e => e.Id).Should().Equal(_secondary.Id, _primary.Id);
        }

        [TestCase(0, 10)]
        [TestCase(-1, 10)]
        [TestCase(1, 0)]
        [TestCase(1, 101)]
        public void invalid_paging_is_rejected(int page, int size)
        {
            Action query = () => Subject.Missing(Page(page, size), true);
            query.Should().Throw<ArgumentException>();
        }

        [Test]
        public void unsupported_sort_expressions_are_rejected()
        {
            Action query = () => Subject.Missing(Page(sort: "e.Id; DELETE FROM Movies"), true);
            query.Should().Throw<ArgumentException>();
            Db.All<MovieEdition>().Should().HaveCount(2);
        }
    }
}
