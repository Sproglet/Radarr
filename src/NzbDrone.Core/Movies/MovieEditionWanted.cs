using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Movies
{
    public class MovieEditionWanted : MovieEdition
    {
        public string MovieTitle { get; set; }
        public int Year { get; set; }
        public int TmdbId { get; set; }
        public bool MovieMonitored { get; set; }
        public int? PrimaryEditionId { get; set; }
        public bool ProfileExists { get; set; }
        public string QualityProfileName { get; set; }
        public int? ResolvedMovieFileId { get; set; }
        public string RelativePath { get; set; }
        public QualityModel Quality { get; set; }
    }
}
