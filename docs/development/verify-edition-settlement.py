"""Validate terminal identity using disposable databases, HTTP clients and media.

Run in the externally offline SDK runner, with optional --postgres/--history-only/--operation-id. All malformed
records are offline synthetic fixtures; this never opens a production database.
"""

import importlib.util
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p


def blocked(media, other_id):
    p.command("RefreshMonitoredDownloads")
    rows = [r for r in p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"] if r.get("downloadId") == c.SHARED]
    assert {r["downloadClientId"] for r in rows} == {41, 42}, rows
    first = next(r for r in rows if r["downloadClientId"] == 41)
    other = next(r for r in rows if r["downloadClientId"] == 42)
    assert first["trackedDownloadState"] == "importBlocked" and first.get("movieEditionId") is None, first
    assert first.get("editionCustomFormatScore") is None, first
    assert any("inconsistent" in message for status in first["statusMessages"] for message in status["messages"]), first
    assert other["id"] == other_id and other["movieEditionId"] == 2 and other["trackedDownloadState"] == "downloading", other
    assert other["editionCustomFormatScore"] == 77, other
    assert p.snapshot() == media


def run(postgres, history_only=False, operation_identity=False):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    media = p.snapshot()
    p.stop()
    clients = [c.SyntheticSab(1), c.SyntheticSab(2)]
    c.SERVERS.extend(clients)
    c.seed(clients, secondary)
    if operation_identity:
        with p.database() as db:
            for table in ("History", "DownloadHistory"):
                for row in p.rows(table):
                    if row["EventType"] == 1:
                        operation = "00000000-0000-4000-8000-0000000000" + ("41" if row["MovieEditionId"] == 1 else "42")
                        db.execute('UPDATE "' + table + '" SET "EditionOperationId"=? WHERE "Id"=?', (operation, row["Id"]))
    grabs = [r for r in p.rows("DownloadHistory") if r["EventType"] == 1]
    first = next(r for r in grabs if r["DownloadClientId"] == 41)
    with p.database() as db:
        if history_only:
            db.execute('DELETE FROM "DownloadHistory" WHERE "Id"=?', (first["Id"],))
        db.execute('UPDATE "MovieEditions" SET "MatchRules"=?,"RuleRevision"="RuleRevision"+1', ('["ChangedSyntheticGroup$"]',))
    grabs = [r for r in p.rows("DownloadHistory") if r["EventType"] == 1]
    movie_grabs = [r for r in p.rows("History") if r["EventType"] == 1]
    p.start()
    initial = c.queue([1, 2])
    other_id = initial[2]["id"]
    p.stop()
    with p.database() as db:
        db.execute('''INSERT INTO "DownloadHistory" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Date","EventType","DownloadId","Protocol","IndexerId","DownloadClientId","Release","Data")
            VALUES (1,NULL,NULL,?,?,2,?,1,77,41,?,?)''',
            (first["SourceTitle"], first["Date"], c.SHARED, json.dumps(json.loads(first["Release"])) if isinstance(first["Release"], str) else json.dumps(first["Release"]), '{}'))
    terminal = next(r for r in p.rows("DownloadHistory") if r["EventType"] == 2)
    cases = [(1, None, None, first["EditionOperationId"], "unassigned"), (1, 1, 2, first["EditionOperationId"], "wrong revision"), (999999, 1, 1, first["EditionOperationId"], "foreign movie")]
    if operation_identity:
        cases.extend([(1, 1, 1, "00000000-0000-4000-8000-000000000099", "wrong operation"), (1, 1, 1, None, "missing operation")])
    for movie, edition, revision, operation, label in cases:
        with p.database() as db:
            db.execute('UPDATE "DownloadHistory" SET "MovieId"=?,"MovieEditionId"=?,"EditionRuleRevision"=?,"EditionOperationId"=? WHERE "Id"=?', (movie, edition, revision, operation, terminal["Id"]))
        p.start()
        blocked(media, other_id)
        p.stop()
        p.start()
        blocked(media, other_id)
        assert [r for r in p.rows("DownloadHistory") if r["EventType"] == 1] == grabs
        assert [r for r in p.rows("History") if r["EventType"] == 1] == movie_grabs
        p.stop()
        print("PASS: " + label + " completion remains blocked across restart, retains saved grabs and leaves the other client/edition and media intact", flush=True)
    with p.database() as db:
        db.execute('UPDATE "DownloadHistory" SET "MovieId"=1,"MovieEditionId"=1,"EditionRuleRevision"=1,"EditionOperationId"=? WHERE "Id"=?', (first["EditionOperationId"], terminal["Id"]))
    p.start()
    remaining = c.queue([2])
    assert remaining[2]["id"] == other_id and p.snapshot() == media
    p.stop()
    p.start()
    assert c.queue([2])[2]["id"] == other_id and p.snapshot() == media
    assert all(not client.deleted and not client.rejected for client in clients)
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: repairing only the saved identity settles the intended client/edition despite later rule edits, survives restart and starts no acquisition, retry or client deletion", flush=True)


if __name__ == "__main__":
    try:
        if any(arg not in {"--postgres", "--history-only", "--operation-id"} for arg in sys.argv[1:]) or len(set(sys.argv[1:])) != len(sys.argv[1:]):
            raise ValueError("Only --postgres, --history-only and --operation-id are supported")
        run("--postgres" in sys.argv[1:], "--history-only" in sys.argv[1:], "--operation-id" in sys.argv[1:])
    finally:
        p.stop()
        try:
            for client in c.SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
