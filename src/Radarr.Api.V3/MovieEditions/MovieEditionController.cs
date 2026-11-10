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
        private readonly IMovieEditionManagementService _management;

        public MovieEditionController(IMovieRepository movies, IMovieEditionRepository editions, IMovieEditionContextService contexts, IMovieEditionNamingPreviewService namingPreview, IMovieEditionManagementService management)
        {
            _movies = movies;
            _editions = editions;
            _contexts = contexts;
            _namingPreview = namingPreview;
            _management = management;
        }

        [HttpGet("{id:int}")]
        [Produces("application/json")]
        public Ok<MovieEditionResource> GetEdition(int id)
        {
            var edition = _editions.Find(id) ?? throw new NotFoundException("Edition not found.");
            var movie = _movies.Find(edition.MovieId) ?? throw new NotFoundException("Movie not found.");
            return TypedResults.Ok(edition.ToResource(movie, _contexts));
        }

        [HttpPost]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Results<Created<MovieEditionResource>, Conflict<object>> Create([FromBody] MovieEditionNamingProposalResource proposal)
        {
            try
            {
                var edition = _management.Create(ToProposal(proposal));
                var movie = _movies.Find(edition.MovieId) ?? throw new NotFoundException("Movie not found.");
                return TypedResults.Created($"{Request.PathBase}/api/v3/movieedition/{edition.Id}", edition.ToResource(movie, _contexts));
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
            catch (InvalidOperationException ex)
            {
                return TypedResults.Conflict<object>(new { message = ex.Message });
            }
        }

        [HttpPost("{id:int}/primary")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Results<Ok<MovieEditionResource>, Conflict<object>> SelectPrimary(int id, [FromBody] MovieEditionPrimaryResource request)
        {
            if (request == null || request.MovieId <= 0 || id <= 0 || request.ExpectedPrimaryEditionId <= 0 ||
                request.ExpectedQualityProfileId <= 0 || request.ExpectedMovieFileId <= 0)
            {
                throw new BadRequestException("Provide positive IDs and an explicit expected file ID, or null for a missing file.");
            }

            try
            {
                var edition = _management.SelectPrimary(request.MovieId, id, request.ExpectedPrimaryEditionId, request.ExpectedMovieFileId, request.ExpectedQualityProfileId);
                var movie = _movies.Get(edition.MovieId);
                return TypedResults.Ok(edition.ToResource(movie, _contexts));
            }
            catch (ArgumentException ex)
            {
                throw new BadRequestException(ex.Message);
            }
            catch (ModelNotFoundException ex)
            {
                throw new NotFoundException(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.Conflict<object>(new { message = ex.Message });
            }
        }

        private static MovieEdition ToProposal(MovieEditionNamingProposalResource proposal)
        {
            if (proposal == null || proposal.QualityProfileId <= 0)
            {
                throw new BadRequestException("Provide a proposal and a positive profile ID, or omit the profile to use the movie default.");
            }

            return new MovieEdition
            {
                MovieId = proposal.MovieId, Name = proposal.Name, QualityProfileId = proposal.QualityProfileId ?? 0,
                Attributes = proposal.Attributes, MatchRules = proposal.MatchRules
            };
        }

        [HttpPost("preview")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public Ok<MovieEditionNamingPreview> Preview([FromBody] MovieEditionNamingProposalResource proposal)
        {
            try
            {
                return TypedResults.Ok(_namingPreview.Preview(ToProposal(proposal)));
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
                resources.Add(edition.ToResource(movie, _contexts));
            }

            return TypedResults.Ok(resources);
        }
    }
}
