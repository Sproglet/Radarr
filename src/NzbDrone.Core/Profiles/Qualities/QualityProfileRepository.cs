using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface IQualityProfileRepository : IBasicRepository<QualityProfile>
    {
        bool Exists(int id);
    }

    public class QualityProfileRepository : BasicRepository<QualityProfile>, IQualityProfileRepository
    {
        private readonly ICustomFormatService _customFormatService;

        public QualityProfileRepository(IMainDatabase database,
                                 IEventAggregator eventAggregator,
                                 ICustomFormatService customFormatService)
            : base(database, eventAggregator)
        {
            _customFormatService = customFormatService;
        }

        protected override bool RequiresLifecycleTransaction => true;
        protected override bool RequiresDeletedIds => true;

        protected override void OnDeleted(IDbConnection connection, IDbTransaction transaction, List<int> ids)
        {
            if (ids.Count == 0)
            {
                return;
            }

            // List<int> has an embedded-document handler; bind scalar IDs on both database engines.
            var parameters = new DynamicParameters();
            var selectedIds = string.Join(", ", ids.Select((id, index) =>
            {
                var name = $"ProfileId{index}";
                parameters.Add(name, id);
                return "@" + name;
            }));
            var used = connection.Query<int>(
                $@"SELECT ""QualityProfileId"" FROM ""MovieEditions"" WHERE ""QualityProfileId"" IN ({selectedIds})
                UNION SELECT ""QualityProfileId"" FROM ""Movies"" WHERE ""QualityProfileId"" IN ({selectedIds})
                UNION SELECT ""QualityProfileId"" FROM ""ImportLists"" WHERE ""QualityProfileId"" IN ({selectedIds})
                UNION SELECT ""QualityProfileId"" FROM ""Collections"" WHERE ""QualityProfileId"" IN ({selectedIds})
                LIMIT 1",
                parameters,
                transaction).FirstOrDefault();
            if (used != 0)
            {
                throw new QualityProfileInUseException(used);
            }
        }

        protected override List<QualityProfile> Query(SqlBuilder builder)
        {
            var cfs = _customFormatService.All().ToDictionary(c => c.Id);

            var profiles = base.Query(builder);

            // Do the conversions from Id to full CustomFormat object here instead of in
            // CustomFormatIntConverter to remove need to for a static property containing
            // all the custom formats
            foreach (var profile in profiles)
            {
                var formatItems = new List<ProfileFormatItem>();

                foreach (var formatItem in profile.FormatItems)
                {
                    // Skip any format that has been removed, but the profile wasn't updated properly
                    if (cfs.ContainsKey(formatItem.Format.Id))
                    {
                        formatItem.Format = cfs[formatItem.Format.Id];

                        formatItems.Add(formatItem);
                    }
                }

                profile.FormatItems = formatItems;
            }

            return profiles;
        }

        public bool Exists(int id)
        {
            return Query(p => p.Id == id).Count == 1;
        }
    }
}
