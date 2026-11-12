using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(246)]
    public class movie_edition_search_time_with_timezone : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfDatabase("PostgreSQL").Execute.Sql(@"
                DO $migration$
                DECLARE column_type text;
                BEGIN
                    SELECT data_type INTO column_type FROM information_schema.columns
                    WHERE table_schema = current_schema() AND table_name = 'MovieEditions' AND column_name = 'LastSearchTime';

                    IF column_type = 'timestamp without time zone' THEN
                        ALTER TABLE ""MovieEditions"" ALTER COLUMN ""LastSearchTime""
                            TYPE timestamp with time zone USING ""LastSearchTime"" AT TIME ZONE 'UTC';
                    ELSIF column_type IS DISTINCT FROM 'timestamp with time zone' THEN
                        RAISE EXCEPTION 'MovieEditions.LastSearchTime must be a timestamp column';
                    END IF;
                END;
                $migration$;");
        }
    }
}
