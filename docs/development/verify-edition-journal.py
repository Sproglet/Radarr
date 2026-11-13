"""Verify durable intent retention using disposable configuration and synthetic media.

Run in the isolated SDK runner; optional --postgres/--browser/--finished-history.
Intent is seeded offline because automatic acquisition and submission stay gated.
Repository preparation and evidence transitions are covered by database fixtures.
"""

import importlib.util
import json
from pathlib import Path
import sys
import subprocess
import time
import urllib.error

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("phase3", Path(__file__).with_name("verify-phase3.py"))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def run(postgres, browser=False, finished_history=False):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    if browser:
        assert (p.APP / "UI" / "index.html").read_bytes() == Path("/src/_output/UI/index.html").read_bytes()
        host = p.api("api/v3/config/host")
        host.update(authenticationMethod="forms", authenticationRequired="disabledForLocalAddresses", allowedHosts="127.0.0.1,localhost",
                    username="synthetic-browser-user", password="synthetic-browser-password", passwordConfirmation="synthetic-browser-password")
        p.api("api/v3/config/host", host, "PUT")
    media = p.snapshot()
    p.stop()
    movie = p.rows("Movies")[0]
    edition = next(row for row in p.rows("MovieEditions") if row["Id"] == 2)
    attributes = edition["Attributes"]
    if not isinstance(attributes, str):
        attributes = json.dumps(attributes)
    release = {"title": "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", "guid": "synthetic-journal-release",
               "downloadUrl": "http://127.0.0.1/synthetic?apikey=synthetic-secret", "downloadProtocol": "usenet"}
    with p.database() as db:
        db.execute('''INSERT INTO "MovieEditionSubmissions" ("OperationId","MovieId","MovieEditionId","EditionRuleRevision",
            "EditionName","EditionAttributes","QualityProfileId","MovieFileId","MoviePath","DownloadClientId","DownloadClientName",
            "Release","ParsedMovieInfo","DownloadId","State","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,NULL,0,?,?)''',
            ("00000000-0000-4000-8000-000000000041", movie["Id"], edition["Id"], edition["RuleRevision"], edition["Name"], attributes,
             edition["QualityProfileId"], edition["MovieFileId"], movie["Path"], 41, "Synthetic Client", json.dumps(release), '{}',
             "2026-11-12T12:00:00Z", "2026-11-12T12:00:00Z"))
        if finished_history:
            for index in range(1, 106):
                db.execute('''INSERT INTO "MovieEditionSubmissions" ("OperationId","MovieId","MovieEditionId","EditionRuleRevision",
                    "EditionName","EditionAttributes","QualityProfileId","MovieFileId","MoviePath","DownloadClientId","DownloadClientName",
                    "Release","ParsedMovieInfo","DownloadId","State","CreatedAt","UpdatedAt")
                    SELECT ?,"MovieId","MovieEditionId","EditionRuleRevision","EditionName","EditionAttributes","QualityProfileId","MovieFileId",
                    "MoviePath","DownloadClientId","DownloadClientName","Release","ParsedMovieInfo",?,3,?,?
                    FROM "MovieEditionSubmissions" WHERE "OperationId"=?''',
                    ("10000000-0000-4000-8000-" + format(index, "012x"), "synthetic-finished-" + str(index),
                     "2026-11-13T12:00:00Z", "2026-11-13T12:00:00Z", "00000000-0000-4000-8000-000000000041"))
    saved = p.rows("MovieEditionSubmissions")
    for restart in range(2):
        p.start()
        p.command("Housekeeping")
        assert p.rows("MovieEditionSubmissions") == saved and p.snapshot() == media
        summary = p.api("api/v3/movieedition/submission?movieId=1")
        assert len(summary) == (101 if finished_history else 1) and summary[0]["operationId"] == saved[0]["OperationId"]
        if finished_history:
            assert all(row["state"] == "settled" for row in summary[1:])
            assert {row["operationId"] for row in summary[1:]} == {
                row["OperationId"] for row in saved[-100:]}
        assert not summary[0]["canReconcile"] and "unknown" in summary[0]["message"].lower()
        exposed = json.dumps(summary).lower()
        assert "synthetic-secret" not in exposed and "historydata" not in exposed and "downloadurl" not in exposed and movie["Path"].lower() not in exposed
        if browser and restart == 0:
            subprocess.run(["node", "/src/docs/development/verify-edition-journal-browser.cjs", "prepared"] + (["--finished-history"] if finished_history else []), check=True)
        for delete_files in (False, True):
            try:
                p.api("api/v3/movie/1?deleteFiles=" + str(delete_files).lower(), method="DELETE")
            except urllib.error.HTTPError as error:
                body = error.read().decode()
                assert error.code == 409, (error.code, body)
                assert "submission intent" in body
            else:
                raise AssertionError("Movie deletion bypassed unresolved submission intent")
            assert p.snapshot() == media and p.rows("MovieEditionSubmissions") == saved
            assert len(p.api("api/v3/movieedition?movieId=1")) == 2
        p.stop()
    print("PASS: prepared intent survives housekeeping and two process restarts with its original operation and edition snapshot", flush=True)
    print("PASS: both movie-deletion modes return 409 before removing either edition's video/subtitle bytes or IDs", flush=True)

    operation = saved[0]["OperationId"]
    endpoint = "api/v3/movieedition/submission/" + operation + "/reconcile"
    expected = {"movieId": 1, "movieEditionId": 2, "expectedState": "accepted", "expectedDownloadId": "synthetic-download"}
    p.start()
    try:
        p.api(endpoint, expected)
    except urllib.error.HTTPError as error:
        assert error.code == 409, (error.code, error.read().decode())
    else:
        raise AssertionError("Prepared unknown outcome was reconciled without acceptance evidence")
    p.stop()
    # Offline synthetic acceptance evidence, never a production client or an API for assigning outcomes.
    parsed = {"quality": {"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}}, "languages": [1]}
    history_data = {"downloadClientId": "41", "movieEditionName": edition["Name"], "customFormatScore": "0"}
    download_data = {"movieEditionName": edition["Name"], "customFormatScore": "0"}
    with p.database() as db:
        db.execute('''UPDATE "MovieEditionSubmissions" SET "State"=1,"DownloadId"='synthetic-download',
            "HistoryData"=?,"DownloadHistoryData"=?,"Languages"='[1]',"ParsedMovieInfo"=? WHERE "OperationId"=?''',
            (json.dumps(history_data), json.dumps(download_data), json.dumps(parsed), operation))
    p.start()
    for _ in range(100):
        recovered = p.api("api/v3/movieedition/submission?movieId=1")[0]
        if recovered["state"] == "recorded":
            break
        time.sleep(0.1)
    else:
        raise AssertionError("Startup did not reconcile saved acceptance")
    expected["expectedState"] = "recorded"
    assert p.api("api/v3/movieedition/submission?movieId=1")[0]["canReconcile"]
    for bad, status in [({**expected, "expectedDownloadId": "synthetic-wrong"}, 409), ({**expected, "movieEditionId": 99999}, 400)]:
        try:
            p.api(endpoint, bad)
        except urllib.error.HTTPError as error:
            assert error.code == status, (error.code, error.read().decode())
        else:
            raise AssertionError("Stale/foreign reconciliation was accepted")
    assert sum(row.get("EditionOperationId") == operation for row in p.rows("History")) == 1
    if browser:
        subprocess.run(["node", "/src/docs/development/verify-edition-journal-browser.cjs", "recorded"] + (["--finished-history"] if finished_history else []), check=True)
        result = p.api("api/v3/movieedition/submission?movieId=1")[0]
    else:
        result = p.api(endpoint, expected)
    assert result["state"] == "recorded" and p.snapshot() == media, result
    before = {table: [row for row in p.rows(table) if row.get("EditionOperationId") == operation] for table in ("History", "DownloadHistory")}
    assert all(len(rows) == 1 for rows in before.values())
    replay = {**expected, "expectedState": "recorded"}
    p.api(endpoint, replay)
    p.stop()
    p.start()
    p.api(endpoint, replay)
    assert before == {table: [row for row in p.rows(table) if row.get("EditionOperationId") == operation] for table in before}
    assert p.snapshot() == media
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "DownloadHistory" SET "SourceTitle"=? WHERE "EditionOperationId"=?', ("Synthetic.Conflicting.Voyage.2026-SyntheticGroup", operation))
    p.start()
    try:
        p.api(endpoint, replay)
    except urllib.error.HTTPError as error:
        assert error.code == 409, (error.code, error.read().decode())
    else:
        raise AssertionError("Conflicting evidence was overwritten during reconciliation")
    assert p.snapshot() == media
    print("PASS: review API hides private payloads, rejects unknown/stale/foreign evidence and reconciles accepted intent without submission", flush=True)
    print("PASS: reconciliation replay preserves both grab IDs across restart and conflicting evidence returns 409 without touching media", flush=True)


if __name__ == "__main__":
    try:
        if any(arg not in {"--postgres", "--browser", "--finished-history"} for arg in sys.argv[1:]) or len(set(sys.argv[1:])) != len(sys.argv[1:]):
            raise ValueError("Only --postgres, --browser and --finished-history are supported")
        run("--postgres" in sys.argv[1:], "--browser" in sys.argv[1:], "--finished-history" in sys.argv[1:])
    finally:
        p.cleanup()
