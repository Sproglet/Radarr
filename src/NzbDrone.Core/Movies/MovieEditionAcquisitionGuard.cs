using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Movies
{
    // Legacy movie-only evaluation cannot honor independently managed preferences,
    // even when a movie currently has just one target. Keep all such acquisition
    // on the same disabled path until its edition-aware safety gates pass.
    public static class MovieEditionAcquisitionGuard
    {
        public static bool RequiresEditionContext(RemoteMovie release, List<MovieEdition> editions)
        {
            if (release.EditionContext != null || release.EditionRecoveryError != null || editions?.Count > 1)
            {
                return true;
            }

            if (release.Movie == null || editions?.Count != 1)
            {
                return false;
            }

            var target = editions[0];
            return target.MovieId != release.Movie.Id || target.QualityProfileId != release.Movie.QualityProfileId ||
                (!target.Monitored && release.Movie.Monitored) || target.MatchRules?.Count > 0 || target.Attributes?.Count > 0;
        }
    }
}
