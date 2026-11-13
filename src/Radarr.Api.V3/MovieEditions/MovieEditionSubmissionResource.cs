using System;
using NzbDrone.Core.Download;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionSubmissionResource
    {
        public string OperationId { get; set; }
        public int MovieId { get; set; }
        public int MovieEditionId { get; set; }
        public string EditionName { get; set; }
        public int EditionRuleRevision { get; set; }
        public string ReleaseTitle { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadClientName { get; set; }
        public string DownloadId { get; set; }
        public MovieEditionSubmissionState State { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool CanReconcile { get; set; }
        public string Message { get; set; }

        public static MovieEditionSubmissionResource FromModel(MovieEditionSubmission model)
        {
            var complete = model.HistoryData != null && model.DownloadHistoryData != null && model.Languages != null &&
                !string.IsNullOrWhiteSpace(model.DownloadId);
            return new MovieEditionSubmissionResource
            {
                OperationId = model.OperationId, MovieId = model.MovieId, MovieEditionId = model.MovieEditionId,
                EditionName = model.EditionName, EditionRuleRevision = model.EditionRuleRevision, ReleaseTitle = model.Release?.Title,
                DownloadClientId = model.DownloadClientId, DownloadClientName = model.DownloadClientName, DownloadId = model.DownloadId,
                State = model.State, CreatedAt = model.CreatedAt, UpdatedAt = model.UpdatedAt,
                CanReconcile = complete && model.State is MovieEditionSubmissionState.Accepted or MovieEditionSubmissionState.Recorded,
                Message = model.State switch
                {
                    MovieEditionSubmissionState.Prepared => "The client outcome is unknown. Review the saved intent and client before any further submission.",
                    MovieEditionSubmissionState.Accepted when complete => "The client accepted this release. Saved download history can be repaired without another client request.",
                    MovieEditionSubmissionState.Recorded when complete => "Download history is recorded. Reconciliation can verify or repair its saved records.",
                    MovieEditionSubmissionState.Settled => "This submission has matching completion, failure or removal evidence.",
                    _ => "The saved evidence requires manual review; automatic reconciliation is unavailable."
                }
            };
        }
    }

    public class MovieEditionSubmissionReconcileResource
    {
        public int? MovieId { get; set; }
        public int? MovieEditionId { get; set; }
        public MovieEditionSubmissionState? ExpectedState { get; set; }
        public string ExpectedDownloadId { get; set; }
    }
}
