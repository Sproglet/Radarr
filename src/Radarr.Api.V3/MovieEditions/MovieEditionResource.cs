using System;
using System.Collections.Generic;
using NzbDrone.Core.Qualities;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionResource : RestResource
    {
        public int MovieId { get; set; }
        public string EditionKey { get; set; }
        public string Name { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
        public bool EffectiveMonitored { get; set; }
        public int QualityProfileId { get; set; }
        public string QualityProfileName { get; set; }
        public int? MovieFileId { get; set; }
        public string RelativePath { get; set; }
        public QualityModel Quality { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public Dictionary<string, string> Attributes { get; set; }
        public List<string> MatchRules { get; set; }
        public int RuleRevision { get; set; }
        public string State { get; set; }
        public string RejectionReason { get; set; }
    }
}
