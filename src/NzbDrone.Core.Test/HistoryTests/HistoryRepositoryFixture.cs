using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.HistoryTests
{
    [TestFixture]
    public class HistoryRepositoryFixture : DbTest<HistoryRepository, MovieHistory>
    {
        private Movie _movie1;
        private Movie _movie2;

        [SetUp]
        public void Setup()
        {
            _movie1 = Builder<Movie>.CreateNew()
                                    .With(s => s.Id = 7)
                                    .Build();

            _movie2 = Builder<Movie>.CreateNew()
                                    .With(s => s.Id = 8)
                                    .Build();
        }

        [Test]
        public void should_read_write_dictionary()
        {
            var history = Builder<MovieHistory>.CreateNew()
                .With(c => c.Quality = new QualityModel())
                .With(c => c.Languages = new List<Language>())
                .BuildNew();

            history.Data.Add("key1", "value1");
            history.Data.Add("key2", "value2");

            Subject.Insert(history);

            StoredModel.Data.Should().HaveCount(2);
        }

        [Test]
        public void should_get_download_history()
        {
            var historyBluray = Builder<MovieHistory>.CreateNew()
                .With(c => c.Quality = new QualityModel(Quality.Bluray1080p))
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.MovieId = 12)
                .With(c => c.MovieEditionId = null)
                .With(c => c.EventType = MovieHistoryEventType.Grabbed)
                .BuildNew();

            var historyDvd = Builder<MovieHistory>.CreateNew()
                .With(c => c.Quality = new QualityModel(Quality.DVD))
                .With(c => c.Languages = new List<Language> { Language.English })
                .With(c => c.MovieId = 12)
                .With(c => c.MovieEditionId = null)
                .With(c => c.EventType = MovieHistoryEventType.Grabbed)
             .BuildNew();

            Subject.Insert(historyBluray);
            Subject.Insert(historyDvd);

            var downloadHistory = Subject.FindDownloadHistory(12, new QualityModel(Quality.Bluray1080p));

            downloadHistory.Should().HaveCount(1);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void history_queries_hydrate_only_the_selected_primary_file_and_preserve_recorded_target(bool primaryHasFile)
        {
            var profile = Db.Insert(new QualityProfile { Name = "Synthetic Default", Items = new(), Language = Language.English });
            var movie = Db.Insert(Builder<Movie>.CreateNew().With(m => m.QualityProfileId = profile.Id)
                .With(m => m.MovieFileId = 0).With(m => m.PrimaryEditionId = null).BuildNew());
            var primary = Db.Insert(new MovieEdition { MovieId = movie.Id, EditionKey = "synthetic-theatrical-imax", Name = "Synthetic Theatrical IMAX", QualityProfileId = profile.Id });
            var editions = Db.Insert(new MovieEdition { MovieId = movie.Id, EditionKey = "synthetic-directors-3d", Name = "Synthetic Directors 3D", QualityProfileId = profile.Id });
            var file = Db.Insert(Builder<MovieFile>.CreateNew().With(f => f.MovieId = movie.Id).With(f => f.MovieEditionId = primary.Id)
                .With(f => f.RelativePath = "Synthetic-SyntheticGroup.mkv").With(f => f.ReleaseGroup = "SyntheticGroup")
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p)).With(f => f.Languages = new List<Language> { Language.English }).BuildNew());
            primary.MovieFileId = primaryHasFile ? file.Id : null;
            Db.Update(primary);
            movie.PrimaryEditionId = primary.Id;
            movie.MovieFileId = primaryHasFile ? file.Id : 0;
            Db.Update(movie);
            var history = Subject.Insert(new MovieHistory
            {
                MovieId = movie.Id, MovieEditionId = editions.Id, SourceTitle = "Synthetic.Lunar.Voyage.2026-SyntheticGroup",
                Date = DateTime.UtcNow, Quality = new QualityModel(Quality.Bluray1080p), Languages = new() { Language.English }
            });
            var page = Subject.GetPaged(new PagingSpec<MovieHistory> { Page = 1, PageSize = 10, SortKey = "date", SortDirection = SortDirection.Descending }, null, null);
            page.TotalRecords.Should().Be(1);
            var results = new[] { page.Records, Subject.GetByMovieId(movie.Id, null), Subject.Since(DateTime.UtcNow.AddMinutes(-1), null) };
            foreach (var rows in results)
            {
                var loaded = rows.Single();
                loaded.Id.Should().Be(history.Id);
                loaded.MovieEditionId.Should().Be(editions.Id);
                loaded.Movie.QualityProfile.Id.Should().Be(profile.Id);
                loaded.Movie.MovieFileId.Should().Be(movie.MovieFileId);
                if (primaryHasFile)
                {
                    loaded.Movie.MovieFile.Id.Should().Be(file.Id);
                    loaded.Movie.MovieFile.MovieEditionId.Should().Be(primary.Id);
                }
                else
                {
                    loaded.Movie.MovieFile.Should().BeNull();
                }
            }
        }
    }
}
