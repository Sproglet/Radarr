using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupOrphanedMovieFilesFixture : DbTest<CleanupOrphanedMovieFiles, MovieFile>
    {
        [Test]
        public void should_delete_files_without_a_parent_movie()
        {
            var movieFile = Builder<MovieFile>.CreateNew()
                                                  .With(h => h.Quality = new QualityModel())
                                                  .With(h => h.Languages = new List<Language> { Language.English })
                                                  .BuildNew();

            Db.Insert(movieFile);
            Subject.Clean();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_delete_unorphaned_movie_files()
        {
            var movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                                                   .All()
                                                   .With(h => h.Quality = new QualityModel())
                                                   .With(h => h.Languages = new List<Language> { Language.English })
                                                   .BuildListOfNew();

            Db.InsertMany(movieFiles);

            var movie = Builder<Movie>.CreateNew()
                                          .With(e => e.MovieFileId = movieFiles.First().Id)
                                          .BuildNew();

            Db.Insert(movie);

            foreach (var movieFile in movieFiles)
            {
                movieFile.MovieId = movie.Id;
                Db.Update(movieFile);
            }

            Subject.Clean();
            AllStoredModels.Should().HaveCount(2);
        }

        [Test]
        public void should_preserve_unassigned_files_when_parent_has_no_current_file()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieFileId = 0)
                .BuildNew();
            Db.Insert(movie);

            var file = Builder<MovieFile>.CreateNew()
                .With(f => f.MovieId = movie.Id)
                .With(f => f.MovieEditionId = null)
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English })
                .BuildNew();
            Db.Insert(file);

            Subject.Clean();

            AllStoredModels.Should().ContainSingle().Which.Id.Should().Be(file.Id);
        }
    }
}
