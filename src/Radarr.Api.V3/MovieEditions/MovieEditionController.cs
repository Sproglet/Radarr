using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.MovieEditions
{
    [V3ApiController]
    public class MovieEditionController : Controller
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieEditionRepository _editions;
        private readonly IMovieEditionContextService _contexts;
        private readonly IMovieEditionNamingPreviewService _namingPreview;

        public MovieEditionController(IMovieRepository movies, IMovieEditionRepository editions, IMovieEditionContextService contexts, IMovieEditionNamingPreviewService namingPreview)
        {
            _movies = movies;
            _editions = editions;
            _contexts = contexts;
            _namingPreview = namingPreview;
        }

        [HttpPost("preview")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Ok<MovieEditionNamingPreview> Preview([FromBody] MovieEditionNamingProposalResource proposal)
        {
            if (proposal == null || proposal.QualityProfileId <= 0)
            {
                throw new BadRequestException("Provide a proposal and a positive profile ID, or omit the profile to use the movie default.");
            }

            try
            {
                return TypedResults.Ok(_namingPreview.Preview(new MovieEdition
                {
                    MovieId = proposal.MovieId, Name = proposal.Name, QualityProfileId = proposal.QualityProfileId ?? 0,
                    Attributes = proposal.Attributes, MatchRules = proposal.MatchRules
                }));
            }
            catch (ArgumentException ex)
            {
                throw new BadRequestException(ex.Message);
            }
            catch (NamingFormatException ex)
            {
                throw new BadRequestException(ex.Message);
            }
            catch (ModelNotFoundException ex)
            {
                throw new NotFoundException(ex.Message);
            }
        }

        [HttpGet]
        [Produces("application/json")]
        public Ok<List<MovieEditionResource>> GetEditions(int movieId)
        {
            if (movieId <= 0)
            {
                throw new BadRequestException("A positive movieId is required.");
            }

            var movie = _movies.Find(movieId);
            if (movie == null)
            {
                throw new NotFoundException("Movie not found.");
            }

            var resources = new List<MovieEditionResource>();
            foreach (var edition in _editions.GetByMovie(movieId)
                .OrderByDescending(e => e.Id == movie.PrimaryEditionId)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id))
            {
                var resource = new MovieEditionResource
                {
                    Id = edition.Id,
                    MovieId = edition.MovieId,
                    EditionKey = edition.EditionKey,
                    Name = edition.Name,
                    IsPrimary = edition.Id == movie.PrimaryEditionId,
                    Monitored = edition.Monitored,
                    EffectiveMonitored = movie.Monitored && edition.Monitored,
                    QualityProfileId = edition.QualityProfileId,
                    MovieFileId = edition.MovieFileId,
                    LastSearchTime = edition.LastSearchTime,
                    Attributes = edition.Attributes,
                    MatchRules = edition.MatchRules,
                    RuleRevision = edition.RuleRevision
                };

                try
                {
                    var context = _contexts.Resolve(movie, edition.Id);
                    resource.QualityProfileName = context.QualityProfile.Name;
                    resource.RelativePath = context.CurrentFile?.RelativePath;
                    resource.Quality = context.CurrentFile?.Quality;
                    resource.State = context.CurrentFile == null ? "missing" : "present";
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
                {
                    resource.State = "invalid";
                    resource.RejectionReason = ex.Message;
                }

                resources.Add(resource);
            }

            return TypedResults.Ok(resources);
        }
    }
}
