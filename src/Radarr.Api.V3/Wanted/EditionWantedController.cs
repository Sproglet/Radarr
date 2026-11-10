using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using Radarr.Http;
using Radarr.Http.Extensions;
using Radarr.Http.REST;

namespace Radarr.Api.V3.Wanted
{
    [V3ApiController("wanted")]
    public class EditionWantedController : Controller
    {
        private readonly IMovieEditionWantedRepository _wanted;
        private readonly IMovieCutoffService _cutoff;

        public EditionWantedController(IMovieEditionWantedRepository wanted, IMovieCutoffService cutoff)
        {
            _wanted = wanted;
            _cutoff = cutoff;
        }

        [HttpGet("editionmissing")]
        [Produces("application/json")]
        public Ok<PagingResource<WantedEditionResource>> Missing([FromQuery] PagingRequestResource paging, bool monitored = true) =>
            Get(paging, monitored, false);

        [HttpGet("editioncutoff")]
        [Produces("application/json")]
        public Ok<PagingResource<WantedEditionResource>> Cutoff([FromQuery] PagingRequestResource paging, bool monitored = true) =>
            Get(paging, monitored, true);

        private Ok<PagingResource<WantedEditionResource>> Get(PagingRequestResource paging, bool monitored, bool cutoff)
        {
            var resource = new PagingResource<WantedEditionResource>(paging);
            resource.SortDirection = paging.SortDirection ?? SortDirection.Ascending;
            var spec = resource.MapToPagingSpec<WantedEditionResource, MovieEditionWanted>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title", "year", "editionName", "lastSearchTime" }, "title");
            if (spec.Page < 1 || spec.PageSize < 1 || spec.PageSize > 100 || !Enum.IsDefined(spec.SortDirection))
            {
                throw new BadRequestException("Use a positive page and a page size from 1 to 100 with a supported sort order.");
            }

            // This follows the legacy wanted quality policy. Custom-format cutoff is a separate, unfinished gate.
            return TypedResults.Ok(spec.ApplyToPage(
                page => cutoff ? _wanted.Cutoff(page, monitored, _cutoff.GetQualitiesBelowCutoff()) : _wanted.Missing(page, monitored),
                edition => edition.ToResource()));
        }
    }
}
