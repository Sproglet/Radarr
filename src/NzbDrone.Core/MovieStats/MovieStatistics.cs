using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MovieStats
{
    public class MovieStatistics : ResultSet
    {
        public int MovieId { get; set; }
        public int MovieFileCount { get; set; }
        public int EditionCount { get; set; }
        public int EditionFileCount { get; set; }
        public int MonitoredEditionCount { get; set; }
        public int MissingMonitoredEditionCount { get; set; }
        public bool HasAnyEditionFile => EditionFileCount > 0;
        public long SizeOnDisk { get; set; }
        public string ReleaseGroupsString { get; set; }
        public string MovieFileQualitiesString { get; set; }

        public List<string> ReleaseGroups
        {
            get
            {
                var releaseGroups = new List<string>();

                if (ReleaseGroupsString.IsNotNullOrWhiteSpace())
                {
                    releaseGroups = ReleaseGroupsString
                        .Split('|')
                        .Distinct()
                        .Where(rg => rg.IsNotNullOrWhiteSpace())
                        .OrderBy(rg => rg)
                        .ToList();
                }

                return releaseGroups;
            }
        }

        public List<Quality> MovieFileQualities
        {
            get
            {
                if (MovieFileQualitiesString.IsNullOrWhiteSpace())
                {
                    return new List<Quality>();
                }

                return MovieFileQualitiesString
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(int.Parse)
                    .Distinct()
                    .Select(Quality.FindById)
                    .ToList();
            }
        }
    }
}
