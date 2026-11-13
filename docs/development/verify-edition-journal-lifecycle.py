"""Verify journal settlement through actual application polling/import/removal.

Run in the isolated SDK runner with completed, failure, ignore or bulk, optionally
--postgres. All intent is seeded offline in the harness's disposable database;
only loopback synthetic SAB APIs are contacted and acquisition remains disabled.
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
original_seed = c.seed
saved = {}


def serialized(value):
    return value if isinstance(value, str) else json.dumps(value)


def seed(clients, secondary_profile):
    original_seed(clients, secondary_profile)
    movies = p.rows("Movies")
    editions = p.rows("MovieEditions")
    grabs = p.rows("DownloadHistory")
    movie_grabs = p.rows("History")
    with p.database() as db:
        for grab in grabs:
            if grab["EventType"] != 1:
                continue
            movie = next(row for row in movies if row["Id"] == grab["MovieId"])
            edition = next(row for row in editions if row["Id"] == grab["MovieEditionId"])
            history = next(row for row in movie_grabs if row["EventType"] == 1 and row["DownloadId"] == grab["DownloadId"]
                           and row["MovieEditionId"] == grab["MovieEditionId"])
            operation = "00000000-0000-4000-8000-0000000000" + str(grab["DownloadClientId"])
            for table, row in (("History", history), ("DownloadHistory", grab)):
                db.execute('UPDATE "' + table + '" SET "EditionOperationId"=? WHERE "Id"=?', (operation, row["Id"]))
            intent = {
                "OperationId": operation, "MovieId": movie["Id"], "MovieEditionId": edition["Id"],
                "EditionRuleRevision": grab["EditionRuleRevision"], "EditionName": edition["Name"],
                "EditionAttributes": serialized(edition["Attributes"]), "QualityProfileId": edition["QualityProfileId"],
                "MovieFileId": edition["MovieFileId"], "MoviePath": movie["Path"], "DownloadClientId": grab["DownloadClientId"],
                "DownloadClientName": "Synthetic SAB " + str(edition["Id"]), "DownloadClient": "Sabnzbd",
                "Release": serialized(grab["Release"]), "ParsedMovieInfo": json.dumps({"quality": json.loads(history["Quality"]), "languages": [1]}),
                "DownloadId": grab["DownloadId"], "State": 2, "CreatedAt": grab["Date"], "UpdatedAt": grab["Date"],
                "HistoryData": serialized(history["Data"]), "DownloadHistoryData": serialized(grab["Data"]), "Languages": "[1]",
                "CustomFormatScore": 0, "MovieMatchType": 0, "ReleaseSource": 0
            }
            assert all(key.isalpha() for key in intent)
            db.execute('INSERT INTO "MovieEditionSubmissions" (' + ','.join('"' + key + '"' for key in intent)
                       + ') VALUES (' + ','.join('?' for _ in intent) + ')', tuple(intent.values()))
    saved.update({row["OperationId"]: row for row in p.rows("MovieEditionSubmissions")})


def verify(scenario):
    expected = {1: 3, 2: 2 if scenario == "failure" else 3}
    rows = p.rows("MovieEditionSubmissions")
    assert len(rows) == 2 and {row["MovieEditionId"]: row["State"] for row in rows} == expected, rows
    for row in rows:
        original = saved[row["OperationId"]]
        assert {key: value for key, value in row.items() if key not in {"State", "UpdatedAt"}} == {
            key: value for key, value in original.items() if key not in {"State", "UpdatedAt"}}
    for restart in range(2):
        p.stop()
        p.start()
        p.command("Housekeeping")
        assert p.rows("MovieEditionSubmissions") == rows
        summary = p.api("api/v3/movieedition/submission?movieId=1")
        assert {row["movieEditionId"]: row["state"] for row in summary} == {
            edition: "settled" if state == 3 else "recorded" for edition, state in expected.items()}
        assert all(not row["canReconcile"] for row in summary if row["state"] == "settled")
    assert all(not client.rejected for client in c.SERVERS)
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: " + scenario + " terminal events settle only their saved operation/client/edition, preserve immutable intent and survive two restarts/housekeeping without acquisition or retry", flush=True)


if __name__ == "__main__":
    try:
        if len(sys.argv) not in {2, 3} or sys.argv[1] not in {"completed", "failure", "ignore", "bulk"} or (len(sys.argv) == 3 and sys.argv[2] != "--postgres"):
            raise ValueError("Use completed, failure, ignore or bulk, optionally --postgres")
        c.seed = seed
        c.run()
        verify(sys.argv[1])
    finally:
        p.stop()
        try:
            for client in c.SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
