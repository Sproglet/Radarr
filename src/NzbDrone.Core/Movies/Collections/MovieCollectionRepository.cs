using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies.Collections
{
    public interface IMovieCollectionRepository : IBasicRepository<MovieCollection>
    {
        public MovieCollection GetByTmdbId(int tmdbId);
        bool UpsertMany(List<MovieCollection> data);
    }

    public class MovieCollectionRepository : BasicRepository<MovieCollection>, IMovieCollectionRepository
    {
        public MovieCollectionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, MovieCollection collection)
        {
            QualityProfileReferenceGuard.Lock(connection, transaction, _database.DatabaseType, collection.QualityProfileId);
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, MovieCollection collection, List<string> properties)
        {
            if (!properties.Contains(nameof(MovieCollection.QualityProfileId)))
            {
                return;
            }

            connection.Execute(@"UPDATE ""Collections"" SET ""QualityProfileId"" = ""QualityProfileId"" WHERE ""Id"" = @Id", new { collection.Id }, transaction);
            var stored = connection.QuerySingle<int>(@"SELECT ""QualityProfileId"" FROM ""Collections"" WHERE ""Id"" = @Id", new { collection.Id }, transaction);
            if (stored != collection.QualityProfileId)
            {
                QualityProfileReferenceGuard.Lock(connection, transaction, _database.DatabaseType, collection.QualityProfileId);
            }
        }

        public MovieCollection GetByTmdbId(int tmdbId)
        {
            return Query(x => x.TmdbId == tmdbId).FirstOrDefault();
        }

        public List<MovieCollection> GetByTmdbId(List<int> tmdbIds)
        {
            return Query(x => Enumerable.Contains(tmdbIds, x.TmdbId));
        }

        public bool UpsertMany(List<MovieCollection> data)
        {
            var existingMetadata = GetByTmdbId(data.Select(x => x.TmdbId).ToList());
            var updateCollectionList = new List<MovieCollection>();
            var addCollectionList = new List<MovieCollection>();

            foreach (var collection in data)
            {
                var existing = existingMetadata.SingleOrDefault(x => x.TmdbId == collection.TmdbId);
                if (existing != null)
                {
                    // populate Id in remote data
                    collection.Id = existing.Id;

                    // responses vary, so try adding remote to what we have
                    if (!collection.Equals(existing))
                    {
                        updateCollectionList.Add(collection);
                    }
                    else
                    {
                    }
                }
                else
                {
                    addCollectionList.Add(collection);
                }
            }

            UpdateMany(updateCollectionList);
            InsertMany(addCollectionList);

            return updateCollectionList.Count > 0 || addCollectionList.Count > 0;
        }
    }
}
