namespace NzbDrone.Core.Movies
{
    public class MovieEditionRemovalChange
    {
        public int MovieId { get; set; }
        public int EditionId { get; set; }
        public int ExpectedPrimaryEditionId { get; set; }
        public bool ExpectedMovieMonitored { get; set; }
        public bool ExpectedEditionMonitored { get; set; }
        public int? ExpectedMovieFileId { get; set; }
        public int ExpectedQualityProfileId { get; set; }
        public int ExpectedRuleRevision { get; set; }
        public string ExpectedName { get; set; }
    }
}
