using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text.Json;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Processes;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.CustomScript;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.NotificationTests
{
    [TestFixture]
    public class EditionNotificationScriptFixture : CoreTest<CustomScript>
    {
        private Movie _movie;
        private MovieEdition _edition;
        private MovieFile _file;
        private StringDictionary _variables;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = "/synthetic/movies", PrimaryEditionId = 1, MovieFileId = 10, QualityProfileId = 1,
                MovieMetadata = new MovieMetadata { Title = "Synthetic Lunar Voyage", Year = 2026, TmdbId = -1000001 } };
            _edition = new MovieEdition { Id = 2, MovieId = 1, Name = "Synthetic Directors 3D", QualityProfileId = 7, RuleRevision = 12,
                Attributes = new Dictionary<string, string> { { "cut", "directors-cut" }, { "stereoscopic", "3d" } } };
            _file = new MovieFile { Id = 20, MovieId = 1, MovieEditionId = 2, Movie = _movie, RelativePath = "Synthetic-SyntheticGroup.mkv",
                Quality = new QualityModel(Quality.Bluray1080p), MediaInfo = new MediaInfoModel() };
            Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns(_edition);
            Mocker.SetConstant<INotificationMovieEditionService>(new NotificationMovieEditionService(Mocker.GetMock<IMovieEditionRepository>().Object));
            Mocker.GetMock<ITagRepository>().Setup(r => r.GetTags(It.IsAny<HashSet<int>>())).Returns(new List<Tag>());
            Mocker.GetMock<IProcessProvider>().Setup(p => p.StartAndCapture(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StringDictionary>()))
                .Callback<string, string, StringDictionary>((path, args, variables) => _variables = variables).Returns(new ProcessOutput());
            Subject.Definition = new NotificationDefinition { Settings = new CustomScriptSettings { Path = "/synthetic/capture.py" } };
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_include_only_the_explicit_grab_assignment(bool assigned)
        {
            var remote = new RemoteMovie { Movie = _movie, Release = new ReleaseInfo { Title = "Synthetic.Release-SyntheticGroup" },
                ParsedMovieInfo = new ParsedMovieInfo(), EditionContext = assigned ? new MovieEditionContext(_edition, new QualityProfile { Id = 7 }, null) : null };
            Subject.OnGrab(new GrabMessage { Movie = _movie, RemoteMovie = remote, Quality = _file.Quality });
            _variables["Radarr_MovieEdition_Id"].Should().Be(assigned ? "2" : "");
            _variables["Radarr_MovieEdition_Name"].Should().Be(assigned ? _edition.Name : "");
        }

        [Test]
        public void should_report_import_and_replaced_file_identity_without_using_the_primary()
        {
            Subject.OnDownload(new DownloadMessage { Movie = _movie, MovieFile = _file, SourcePath = "/synthetic/downloads/Synthetic.mkv",
                MovieInfo = new LocalMovie(), OldMovieFiles = new List<DeletedMovieFile> { new DeletedMovieFile(_file, null) } });
            _variables["Radarr_MovieEdition_Id"].Should().Be("2");
            _variables["Radarr_MovieEdition_QualityProfileId"].Should().Be("7");
            using var json = JsonDocument.Parse(_variables["Radarr_DeletedMovieFile_Editions"]);
            json.RootElement[0].GetProperty("movieFileId").GetInt32().Should().Be(20);
            json.RootElement[0].GetProperty("movieEditionId").GetInt32().Should().Be(2);
            json.RootElement[0].GetProperty("movieEdition").GetProperty("ruleRevision").GetInt32().Should().Be(12);
            _movie.MovieFileId.Should().Be(10);
        }

        [Test]
        public void should_preserve_alignment_and_explicit_nulls_in_mixed_rename_events()
        {
            var other = new MovieFile { Id = 21, MovieId = 1, Movie = _movie, RelativePath = "SyntheticUnassigned.mkv" };
            Subject.OnMovieRename(_movie, new List<RenamedMovieFile> { new RenamedMovieFile { MovieFile = _file }, new RenamedMovieFile { MovieFile = other } });
            _variables["Radarr_MovieFile_Ids"].Should().Be("20,21");
            _variables["Radarr_MovieFile_MovieEditionIds"].Should().Be("2,");
            using var json = JsonDocument.Parse(_variables["Radarr_MovieFile_Editions"]);
            json.RootElement[0].GetProperty("movieEditionId").GetInt32().Should().Be(2);
            // JSON serializer may omit null object properties; the aligned ID string
            // and file ID still explicitly identify the unassigned row.
            json.RootElement[1].GetProperty("movieFileId").GetInt32().Should().Be(21);
            json.RootElement[1].TryGetProperty("movieEditionId", out var id).Should().BeFalse();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_preserve_unknown_or_unassigned_delete_identity(bool unassigned)
        {
            if (unassigned) _file.MovieEditionId = null;
            else Mocker.GetMock<IMovieEditionRepository>().Setup(r => r.Find(2)).Returns((MovieEdition)null);
            Subject.OnMovieFileDelete(new MovieFileDeleteMessage { Movie = _movie, MovieFile = _file });
            _variables["Radarr_MovieEdition_Id"].Should().Be(unassigned ? "" : "2");
            _variables["Radarr_MovieEdition_Name"].Should().BeEmpty();
            _variables["Radarr_MovieEdition_QualityProfileId"].Should().BeEmpty();
            if (!unassigned) _variables["Radarr_MovieEdition_Error"].Should().NotBeNullOrWhiteSpace();
        }
    }
}
