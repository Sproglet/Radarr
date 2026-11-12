using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public enum MovieEditionSubmissionState
    {
        Prepared = 0,
        Accepted = 1,
        Recorded = 2,
        Settled = 3
    }

    public class MovieEditionSubmission : ModelBase
    {
        public string OperationId { get; set; }
        public int MovieId { get; set; }
        public int MovieEditionId { get; set; }
        public int EditionRuleRevision { get; set; }
        public string EditionName { get; set; }
        public Dictionary<string, string> EditionAttributes { get; set; } = new();
        public int QualityProfileId { get; set; }
        public int? MovieFileId { get; set; }
        public string MoviePath { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadClientName { get; set; }
        public ReleaseInfo Release { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public string DownloadId { get; set; }
        public MovieEditionSubmissionState State { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
