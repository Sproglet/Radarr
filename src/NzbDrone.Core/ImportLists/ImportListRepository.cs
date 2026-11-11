using System.Collections.Generic;
using System.Data;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists
{
    public interface IImportListRepository : IProviderRepository<ImportListDefinition>
    {
        void UpdateSettings(ImportListDefinition model);
    }

    public class ImportListRepository : ProviderRepository<ImportListDefinition>, IImportListRepository
    {
        public ImportListRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool RequiresLifecycleTransaction => true;

        protected override void OnInserted(IDbConnection connection, IDbTransaction transaction, ImportListDefinition list)
        {
            QualityProfileReferenceGuard.Lock(connection, transaction, _database.DatabaseType, list.QualityProfileId);
        }

        protected override void OnUpdating(IDbConnection connection, IDbTransaction transaction, ImportListDefinition list, List<string> properties)
        {
            if (!properties.Contains(nameof(ImportListDefinition.QualityProfileId))) return;
            connection.Execute(@"UPDATE ""ImportLists"" SET ""QualityProfileId"" = ""QualityProfileId"" WHERE ""Id"" = @Id", new { list.Id }, transaction);
            var stored = connection.QuerySingle<int>(@"SELECT ""QualityProfileId"" FROM ""ImportLists"" WHERE ""Id"" = @Id", new { list.Id }, transaction);
            if (stored != list.QualityProfileId)
            {
                QualityProfileReferenceGuard.Lock(connection, transaction, _database.DatabaseType, list.QualityProfileId);
            }
        }

        public void UpdateSettings(ImportListDefinition model)
        {
            SetFields(model, m => m.Settings);
        }
    }
}
