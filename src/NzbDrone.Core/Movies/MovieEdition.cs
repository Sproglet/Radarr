using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies
{
    public class MovieEdition : ModelBase
    {
        public int MovieId { get; set; }
        public string EditionKey { get; set; }
        public string Name { get; set; }
        public bool Monitored { get; set; }
        public int QualityProfileId { get; set; }
        public int? MovieFileId { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public List<string> MatchRules { get; set; } = new List<string>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public int RuleRevision { get; set; } = 1;
    }
}
