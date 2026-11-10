using System.Collections.Generic;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieEditions
{
    public class MovieEditionNamingProposalResource : RestResource
    {
        public int MovieId { get; set; }
        public string Name { get; set; }
        public int? QualityProfileId { get; set; }
        public Dictionary<string, string> Attributes { get; set; }
        public List<string> MatchRules { get; set; }
    }
}
