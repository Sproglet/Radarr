"""Round-trip edition data using disposable backup artifacts and fresh databases.

Run in the disposable SDK runner with Python/psycopg2, ffmpeg/ffprobe, a built
application and external networking disabled. PostgreSQL additionally requires
pg_dump/pg_restore 16 and the dedicated radarr-edition-postgres Docker alias.
SQLite uses the real application backup archive. PostgreSQL's application backup
is config-only, so its database is restored from a separate native dump.
SQLite exercises the real restore-upload endpoint and startup restoration;
PostgreSQL uses offline native restoration. Add --operation-id to verify shared
grab IDs and restored uniqueness indexes. The browser restore UI is not tested.
"""

import importlib.util
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import urllib.request
import uuid
import zipfile

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p
TABLES = ["Movies", "MovieEditions", "MovieFiles", "SubtitleFiles", "QualityProfiles", "CustomFormats",
          "History", "DownloadHistory", "DownloadClients", "PendingReleases"]


def stored():
    return {table: p.rows(table) for table in TABLES}


def seed_pending(clients):
    stamp = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    with p.database() as db:
        # Disable scheduled RSS in this disposable configuration. Recovery reads
        # pending intent; no indexer lookup or acquisition is needed for this test.
        db.execute('DELETE FROM "Config" WHERE "Key"=?', ("rsssyncinterval",))
        db.execute('INSERT INTO "Config" ("Key","Value") VALUES (?,?)', ("rsssyncinterval", "0"))
        for eid, label, name in [(1, "Theatrical.IMAX", "Theatrical IMAX"), (2, "Directors.Cut.3D", "Directors Cut 3D")]:
            title = "Synthetic.Pending.Lunar.Voyage.2026." + label + ".1080p.BluRay-SyntheticGroup"
            parsed = {"movieTitles": ["Synthetic Lunar Voyage"], "originalTitle": title, "releaseTitle": title,
                "simpleReleaseTitle": "A.Movie.2026." + label + ".1080p.BluRay-SyntheticGroup",
                "quality": {"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}},
                "languages": [1], "releaseGroup": "SyntheticGroup", "year": 2026,
                "edition": "IMAX" if eid == 1 else "Directors Cut"}
            release = {"title": title, "guid": "synthetic-backup-pending-" + str(eid), "indexer": "Synthetic Indexer", "indexerId": 77,
                "downloadProtocol": "usenet", "size": 104857600, "publishDate": stamp,
                "downloadUrl": "http://127.0.0.1:" + str(clients[eid - 1].port) + "/synthetic-pending-never-download"}
            additional = {"editionName": name, "movieMatchType": 0, "releaseSource": 1}
            db.execute('''INSERT INTO "PendingReleases" ("MovieId","MovieEditionId","EditionRuleRevision","Title","Added","ParsedMovieInfo","Release","Reason","AdditionalInfo")
                VALUES (1,?,1,?,?,?,?,0,?)''', (eid, title, stamp, json.dumps(parsed), json.dumps(release), json.dumps(additional)))


def pending_queue():
    records = p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"]
    pending = [row for row in records if row["title"].startswith("Synthetic.Pending.")]
    assert len(pending) == 2 and {row["movieEditionId"] for row in pending} == {1, 2}, pending
    for row in pending:
        assert row["customFormatScore"] == row["editionCustomFormatScore"] == {1: 11, 2: 77}[row["movieEditionId"]], row
    return {row["movieEditionId"]: row["id"] for row in pending}


def native(tool, database, *arguments):
    # Never accept connection settings or database names from a live config.
    assert database.postgres and database.main in database.created
    assert database.options["host"] == "radarr-edition-postgres"
    env = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
    env.update(PGPASSWORD=database.options["password"], PGCONNECT_TIMEOUT="10")
    subprocess.run([tool, "--host=radarr-edition-postgres", "--port=5432", "--username=postgres",
                    "--dbname=" + database.main, *arguments], env=env, check=True)


def download(path):
    request = urllib.request.Request("http://127.0.0.1:17878" + path, headers={"X-Api-Key": p.KEY})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()


def restore_upload(archive):
    boundary = "synthetic-edition-" + uuid.uuid4().hex
    body = ("--" + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="synthetic-backup.zip"\r\n'
            "Content-Type: application/zip\r\n\r\n").encode() + archive + ("\r\n--" + boundary + "--\r\n").encode()
    request = urllib.request.Request("http://127.0.0.1:17878/api/v3/system/backup/restore/upload", data=body,
        headers={"X-Api-Key": p.KEY, "Content-Type": "multipart/form-data; boundary=" + boundary}, method="POST")
    with urllib.request.urlopen(request, timeout=30) as response:
        import json
        assert json.loads(response.read())["restartRequired"] is True


def operation_rows():
    return {table: [row for row in p.rows(table) if row.get("EditionOperationId") is not None]
            for table in ("History", "DownloadHistory")}


def verify_operation_indexes(expected):
    assert p.PROCESS is None or p.PROCESS.poll() is not None
    assert operation_rows() == expected
    for table, rows in expected.items():
        grab = next(row for row in rows if row["EventType"] == 1)
        columns = ",".join('"' + name + '"' for name in grab if name != "Id")
        try:
            with p.database() as db:
                db.execute('INSERT INTO "' + table + '" (' + columns + ') SELECT ' + columns +
                           ' FROM "' + table + '" WHERE "Id"=?', (grab["Id"],))
        except Exception as error:
            if p.DATABASE.postgres:
                import psycopg2.errors
                assert isinstance(error, psycopg2.errors.UniqueViolation), error
            else:
                assert isinstance(error, sqlite3.IntegrityError), error
        else:
            raise AssertionError("Restored operation index accepted a duplicate grab in " + table)
    assert operation_rows() == expected


def run(postgres, operation_identity=False):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    empty = p.api("api/v3/movieedition", {"movieId": 1, "name": "Synthetic Backup Collector 3D IMAX",
        "attributes": {"stereoscopic": "3d", "framing": "imax", "branding": "Synthetic Backup Collector"},
        "matchRules": ["SyntheticBackupCollector"]})
    clients = [c.SyntheticSab(1), c.SyntheticSab(2)]
    c.SERVERS.extend(clients)
    p.stop()
    c.seed(clients, secondary)
    if operation_identity:
        with p.database() as db:
            for table in ("History", "DownloadHistory"):
                for row in p.rows(table):
                    if row["EventType"] == 1:
                        operation = "00000000-0000-4000-8000-0000000000" + ("41" if row["MovieEditionId"] == 1 else "42")
                        db.execute('UPDATE "' + table + '" SET "EditionOperationId"=? WHERE "Id"=?', (operation, row["Id"]))
    with p.database() as db:
        for eid in [1, 2, empty["id"]]:
            db.execute('UPDATE "MovieEditions" SET "LastSearchTime"=?,"MatchRules"=?,"RuleRevision"=? WHERE "Id"=?',
                       ("2026-11-11T09:00:00Z", '["SyntheticNoLongerMatches' + str(eid) + '$"]', 10 + eid, eid))
    seed_pending(clients)
    p.start()
    p.api("api/v3/movieedition/2/monitoring", c.monitoring_request(2, True))
    p.api("api/v3/movieedition/2/primary", {"movieId": 1, "expectedPrimaryEditionId": 1,
        "expectedMovieFileId": p.snapshot()[2][0], "expectedQualityProfileId": secondary})
    before_queue = c.queue([1, 2])
    before_pending = pending_queue()
    before_media = p.snapshot()
    before = stored()
    before_operations = operation_rows()
    if operation_identity:
        for table, rows in before_operations.items():
            assert len(rows) == 2 and {row["EditionOperationId"] for row in rows} == {
                "00000000-0000-4000-8000-000000000041", "00000000-0000-4000-8000-000000000042"}, (table, rows)
    assert before["Movies"][0]["QualityProfileId"] == 1 and before["Movies"][0]["PrimaryEditionId"] == 2
    assert before["MovieEditions"][1]["QualityProfileId"] == secondary and before["MovieEditions"][1]["Monitored"]
    assert len([row for row in before["History"] if row.get("DownloadId") == c.SHARED]) == 2
    assert len(before["DownloadHistory"]) == 2
    assert {(row["MovieEditionId"], row["EditionRuleRevision"]) for row in before["PendingReleases"]} == {(1, 1), (2, 1)}

    # Exercise the actual application backup command and list/download endpoints.
    p.command("Backup")
    backups = p.api("api/v3/system/backup")
    assert len(backups) == 1 and backups[0]["type"] == "manual", backups
    archive = p.CONFIG / "Backups" / "manual" / backups[0]["name"]
    assert archive.is_file() and not archive.is_symlink() and archive.resolve().is_relative_to(p.CONFIG.resolve())
    archive_bytes = archive.read_bytes()
    assert download(backups[0]["path"]) == archive_bytes
    with zipfile.ZipFile(archive) as backup:
        expected = {"config.xml", "INFO"} | (set() if postgres else {"radarr.db"})
        assert set(backup.namelist()) == expected, backup.namelist()
        assert backup.testzip() is None
        payload = {name: backup.read(name) for name in expected}
    assert payload["config.xml"] == (p.CONFIG / "config.xml").read_bytes()
    assert before == stored() and before_media == p.snapshot()
    print("PASS: real application backup preserves state; archive contains " + ", ".join(sorted(expected)), flush=True)

    p.stop()
    source_database = p.DATABASE
    source_config = p.CONFIG
    try:
        if postgres:
            dump = p.ROOT / "synthetic-edition-backup.dump"
            native("pg_dump", source_database, "--format=custom", "--no-owner", "--no-acl", "--file=" + str(dump))
        # Change only the source database after the backup. The restored process
        # must recover the archived value, proving it uses the fresh destination.
        with source_database.connect() as db:
            db.execute('UPDATE "MovieEditions" SET "Name"=? WHERE "Id"=?', ("Synthetic Changed After Backup", empty["id"]))
        p.CONFIG = p.ROOT / "restored-config"
        p.CONFIG.mkdir()
        assert p.CONFIG != source_config and p.CONFIG.resolve().is_relative_to(p.ROOT)
        (p.CONFIG / "config.xml").write_bytes(payload["config.xml"])
        p.DATABASE = p._database_module.TestDatabase(p.CONFIG, lambda: p.PROCESS is None or p.PROCESS.poll() is not None)
        p.database = p.DATABASE.connect
        if postgres:
            p.DATABASE.enable_postgres()
            assert set(p.DATABASE.created).isdisjoint(source_database.created)
            native("pg_restore", p.DATABASE, "--exit-on-error", "--no-owner", "--no-acl", str(dump))
        else:
            p.start()
            assert not p.rows("Movies") and not p.rows("MovieEditions")
            restore_upload(archive_bytes)
            p.stop()
            # Startup, rather than fixture SQL, must apply the staged database.
            p.start()
            p.stop()
        assert stored() == before
        if operation_identity:
            verify_operation_indexes(before_operations)
            assert stored() == before
            print("PASS: restored shared operation IDs retain their saved client/edition/revision, and both restored indexes reject duplicate grabs transactionally", flush=True)
        assert next(e for e in source_database.rows("MovieEditions") if e["Id"] == empty["id"])["Name"] == "Synthetic Changed After Backup"
        p.start()
        assert stored() == before and p.snapshot() == before_media
        restored_queue = c.queue([1, 2])
        assert pending_queue() == before_pending
        assert {eid: row["id"] for eid, row in restored_queue.items()} == {eid: row["id"] for eid, row in before_queue.items()}
        assert stored() == before
        for file in p.api("api/v3/moviefile?movieId=1"):
            assert file["customFormatScore"] == {1: 11, 2: 77}[file["movieEditionId"]]
        p.command("RescanMovie", movieId=1)
        p.command("Housekeeping")
        assert pending_queue() == before_pending
        assert stored() == before and p.snapshot() == before_media
        print("PASS: fresh destination restores combined/custom attributes, rules/revisions, profiles, monitoring/search dates, primary projection, file/subtitle ownership and recorded client-qualified assignments", flush=True)
        print("PASS: both same-ID synthetic jobs recover with stable queue IDs and independent scores after restoration; scan/housekeeping preserve media bytes and IDs", flush=True)
        print("PASS: nonempty pending releases restore assigned editions, original revisions/name snapshots and stable separate queue IDs/scores despite later rule edits, through startup/scan/housekeeping", flush=True)
        if not postgres:
            print("PASS: SQLite restore-upload stages the built-in archive and actual process startup applies it to the fresh configuration", flush=True)

        # New API writes prove restored identity sequences work and cannot write
        # back to the deliberately changed source database.
        added = p.api("api/v3/movieedition", {"movieId": 1, "name": "Synthetic After Restore",
            "attributes": {"branding": "Synthetic After Restore"}})
        assert added["id"] > max(row["Id"] for row in before["MovieEditions"])
        assert not any(row["Id"] == added["id"] for row in source_database.rows("MovieEditions"))
        for target, colour in [(1, "white"), (2, "purple")]:
            previous = p.snapshot()
            p.manual(target, "SyntheticRestoreUpgrade" + str(target), colour)
            current = p.snapshot()
            assert current[3 - target] == previous[3 - target]
            assert current[target][0] != previous[target][0] and current[target][2] != previous[target][2]
            p.command("RescanMovie", movieId=1)
            p.command("RenameMovie", movieIds=[1])
            p.stop()
            p.start()
            assert p.snapshot() == current
            if operation_identity:
                assert operation_rows() == before_operations
            editions = p.api("api/v3/movieedition?movieId=1")
            assert {e["id"]: e["ruleRevision"] for e in editions if e["id"] in [1, 2]} == {1: 11, 2: 12}
            assert [e["id"] for e in editions if e["isPrimary"]] == [2]
        assert all(not client.deleted and not client.rejected for client in clients)
        assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
        print("PASS: restored identity sequences accept new targets; both editions upgrade independently and survive scan/rename/process restart without acquisition", flush=True)
    finally:
        p.stop()
        source_database.cleanup()


if __name__ == "__main__":
    try:
        if any(arg not in {"--postgres", "--operation-id"} for arg in sys.argv[1:]) or len(set(sys.argv[1:])) != len(sys.argv[1:]):
            raise ValueError("Only --postgres and --operation-id are supported")
        run("--postgres" in sys.argv[1:], "--operation-id" in sys.argv[1:])
    finally:
        p.stop()
        try:
            for client in c.SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
