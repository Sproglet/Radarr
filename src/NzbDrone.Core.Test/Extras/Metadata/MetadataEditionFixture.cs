using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras.Metadata
{
    public class MetadataEditionFixture : CoreTest<MetadataService>
    {
        [Test]
        public void import_should_not_overwrite_metadata_owned_by_another_file_or_consumer()
        {
            var movie = new Movie { Id = 1, Path = TempFolder, PrimaryEditionId = 1 };
            var file = new MovieFile { Id = 10, MovieId = 1, MovieEditionId = 1, RelativePath = "Synthetic Theatrical.mkv" };
            var consumer = Mocker.GetMock<IMetadata>();
            consumer.Setup(c => c.MovieMetadata(movie, file)).Returns(new MetadataFileResult("shared.nfo", "Synthetic replacement"));
            Mocker.GetMock<IMetadataFactory>().Setup(f => f.Enabled()).Returns(new List<IMetadata> { consumer.Object });
            Mocker.GetMock<IMetadataFileService>().Setup(s => s.GetFilesByMovie(1)).Returns(new List<MetadataFile>
            {
                new MetadataFile { Id = 20, MovieId = 1, MovieFileId = 11, Consumer = "AnotherSyntheticConsumer", RelativePath = "shared.nfo" }
            });

            Subject.CreateAfterMovieImport(movie, file).Should().BeEmpty();

            Mocker.GetMock<IDiskProvider>().Verify(d => d.WriteAllText(Path.Combine(TempFolder, "shared.nfo"), It.IsAny<string>()), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
