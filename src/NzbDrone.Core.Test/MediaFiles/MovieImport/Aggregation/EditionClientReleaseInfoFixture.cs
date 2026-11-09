using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Aggregation
{
    public class EditionClientReleaseInfoFixture : CoreTest<AggregateReleaseInfo>
    {
        [TestCase(41)]
        [TestCase(42)]
        public void grabbed_release_metadata_should_come_only_from_the_selected_client(int clientId)
        {
            const string downloadId = "synthetic-shared-release-id";
            var selected = new MovieHistory
            {
                MovieId = 1, MovieEditionId = clientId - 40,
                EventType = MovieHistoryEventType.Grabbed,
                SourceTitle = "Synthetic.Lunar.Voyage.2026-Client" + clientId,
                Data = new Dictionary<string, string> { { "indexer", "Synthetic Indexer " + clientId }, { "size", "104857600" } }
            };
            var unrelated = new MovieHistory
            {
                MovieId = 1, MovieEditionId = 43 - clientId,
                EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow.AddDays(1),
                SourceTitle = "Synthetic.Unrelated.Voyage.2026-SyntheticGroup",
                Data = new Dictionary<string, string>()
            };
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(downloadId)).Returns(new List<MovieHistory> { unrelated });
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(clientId, downloadId)).Returns(new List<MovieHistory> { selected });
            var result = Subject.Aggregate(new LocalMovie(), new DownloadClientItem
            {
                DownloadId = downloadId, DownloadClientInfo = new DownloadClientItemClientInfo { Id = clientId }
            });
            result.Release.Title.Should().Be(selected.SourceTitle);
            result.Release.Indexer.Should().Be("Synthetic Indexer " + clientId);
            result.Release.Size.Should().Be(104857600);
            Mocker.GetMock<IHistoryService>().Verify(s => s.FindByDownloadId(downloadId), Times.Never());
        }
    }
}
