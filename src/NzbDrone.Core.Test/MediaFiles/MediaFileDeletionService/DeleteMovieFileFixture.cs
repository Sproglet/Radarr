using System;
using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MediaFileDeletionService
{
    [TestFixture]
    public class DeleteMovieFileFixture : CoreTest<Core.MediaFiles.MediaFileDeletionService>
    {
        private string RootFolder;
        private Movie _movie;
        private MovieFile _movieFile;

        [SetUp]
        public void Setup()
        {
            RootFolder = Path.Combine(TempFolder, "movies");
            _movie = Builder<Movie>.CreateNew()
                                     .With(s => s.Path = Path.Combine(RootFolder, "Movie Title"))
                                     .Build();

            _movieFile = Builder<MovieFile>.CreateNew()
                                               .With(f => f.MovieId = _movie.Id)
                                               .With(f => f.MovieEditionId = 2)
                                               .With(f => f.RelativePath = "Some SubFolder")
                                               .With(f => f.Path = Path.Combine(_movie.Path, "Some SubFolder"))
                                               .Build();

            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(_movieFile.Id)).Returns(() => _movieFile);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(_movie.Id)).Returns(() => new List<MovieFile> { _movieFile });
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(_movie.Id)).Returns(() => _movie);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetParentFolder(_movie.Path))
                  .Returns(RootFolder);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetParentFolder(_movieFile.Path))
                  .Returns(_movie.Path);
        }

        private void RejectsWithoutDeleting(Action action)
        {
            Assert.Throws<NzbDroneClientException>(() => action());
            Mocker.GetMock<IRecycleBinProvider>().Verify(s => s.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [Test]
        public void rejects_a_file_from_another_parent_before_filesystem_or_database_deletion()
        {
            _movieFile.MovieId = _movie.Id + 1;
            RejectsWithoutDeleting(() => Subject.DeleteMovieFile(_movie, _movieFile));
        }

        [TestCase("../Synthetic Other Movie/file.mkv")]
        [TestCase("/tmp/Synthetic Other Movie/file.mkv")]
        [TestCase("")]
        public void rejects_escaping_or_empty_paths_before_deletion(string path)
        {
            _movieFile.RelativePath = path;
            RejectsWithoutDeleting(() => Subject.DeleteMovieFile(_movie, _movieFile));
        }

        [TestCase("missing-file")]
        [TestCase("missing-parent")]
        [TestCase("edition")]
        [TestCase("file-parent")]
        [TestCase("path")]
        [TestCase("folder")]
        public void rejects_changed_snapshot_before_deletion(string change)
        {
            var current = new MovieFile
            {
                Id = _movieFile.Id, MovieId = _movie.Id, RelativePath = _movieFile.RelativePath, MovieEditionId = _movieFile.MovieEditionId
            };
            var parent = new Movie { Id = _movie.Id, Path = _movie.Path };
            if (change == "edition") { current.MovieEditionId++; }
            if (change == "file-parent") { current.MovieId++; }
            if (change == "path") { current.RelativePath = "Synthetic Replacement.mkv"; }
            if (change == "folder") { parent.Path += "-moved"; }
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(_movieFile.Id)).Returns(change == "missing-file" ? null : current);
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(_movie.Id)).Returns(change == "missing-parent" ? null : parent);
            RejectsWithoutDeleting(() => Subject.DeleteMovieFile(_movie, _movieFile));
        }

        [Test]
        public void rejects_duplicate_physical_path_ownership_before_deletion()
        {
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(_movie.Id)).Returns(new List<MovieFile>
            {
                _movieFile, new() { Id = _movieFile.Id + 1, MovieId = _movie.Id, MovieEditionId = 3, RelativePath = _movieFile.RelativePath.ToUpperInvariant() }
            });
            RejectsWithoutDeleting(() => Subject.DeleteMovieFile(_movie, _movieFile));
        }

        private void GivenRootFolderExists()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(RootFolder))
                  .Returns(true);
        }

        private void GivenRootFolderHasFolders()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetDirectories(RootFolder))
                  .Returns(new[] { _movie.Path });
        }

        private void GivenMovieFolderExists()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(_movie.Path))
                  .Returns(true);
        }

        [Test]
        public void should_throw_if_root_folder_does_not_exist()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFile(_movie, _movieFile));
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_should_throw_if_root_folder_is_empty()
        {
            GivenRootFolderExists();

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFile(_movie, _movieFile));
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_delete_from_db_if_movie_folder_does_not_exist()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();

            Subject.DeleteMovieFile(_movie, _movieFile);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_movieFile, DeleteMediaFileReason.Manual), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_movieFile.Path, It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_delete_from_db_if_movie_file_does_not_exist()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenMovieFolderExists();

            Subject.DeleteMovieFile(_movie, _movieFile);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_movieFile, DeleteMediaFileReason.Manual), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_movieFile.Path, It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_delete_from_disk_and_db_if_movie_file_exists()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenMovieFolderExists();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(_movieFile.Path))
                  .Returns(true);

            Subject.DeleteMovieFile(_movie, _movieFile);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_movieFile.Path, "Movie Title"), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_movieFile, DeleteMediaFileReason.Manual), Times.Once());
        }

        [Test]
        public void should_handle_error_deleting_movie_file()
        {
            GivenRootFolderExists();
            GivenRootFolderHasFolders();
            GivenMovieFolderExists();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(_movieFile.Path))
                  .Returns(true);

            Mocker.GetMock<IRecycleBinProvider>()
                  .Setup(s => s.DeleteFile(_movieFile.Path, "Movie Title"))
                  .Throws(new IOException());

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFile(_movie, _movieFile));

            ExceptionVerification.ExpectedErrors(1);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_movieFile.Path, "Movie Title"), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_movieFile, DeleteMediaFileReason.Manual), Times.Never());
        }
    }
}
