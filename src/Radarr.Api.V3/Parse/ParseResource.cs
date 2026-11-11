using System.Collections.Generic;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using Radarr.Api.V3.CustomFormats;
using Radarr.Api.V3.Movies;
using Radarr.Http.REST;

namespace Radarr.Api.V3.Parse
{
    public class ParseResource : RestResource
    {
        public string Title { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public MovieResource Movie { get; set; }
        public List<Language> Languages { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int? CustomFormatScore { get; set; }
        public int? MovieEditionId { get; set; }
        public string MovieEditionName { get; set; }
        public int? QualityProfileId { get; set; }
        public string QualityProfileName { get; set; }
        public bool EditionSelectionExplicit { get; set; }
        public EditionClassification EditionClassification { get; set; }
    }
}
