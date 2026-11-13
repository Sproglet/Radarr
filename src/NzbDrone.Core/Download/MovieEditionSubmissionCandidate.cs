namespace NzbDrone.Core.Download
{
    public class MovieEditionSubmissionCandidate
    {
        public int Id { get; set; }
        public string OperationId { get; set; }
        public bool HasTerminalEvidence { get; set; }
    }
}
