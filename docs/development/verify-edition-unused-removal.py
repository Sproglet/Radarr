"""Verify guarded unused-target removal with disposable configuration/media only.

Run inside the externally offline SDK runner, optionally --postgres. Requires the
built UI, Node and test Chromium. Saved-reference fixtures are seeded only while
this harness's own temporary Radarr process is stopped; no real clients are used.
"""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import urllib.error

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p


def expected(edition):
    movie = p.api("api/v3/movie/1")
    primary = next(edition for edition in p.api("api/v3/movieedition?movieId=1") if edition["isPrimary"])
    return {"movieId": 1, "expectedPrimaryEditionId": primary["id"], "expectedMovieMonitored": movie["monitored"],
            "expectedEditionMonitored": edition["monitored"], "expectedMovieFileId": edition.get("movieFileId"),
            "expectedQualityProfileId": edition["qualityProfileId"], "expectedRuleRevision": edition["ruleRevision"], "expectedName": edition["name"]}


def create(label):
    return p.api("api/v3/movieedition", {"movieId": 1, "name": label, "qualityProfileId": 1,
        "attributes": {"stereoscopic": "3d", "framing": "imax", "custom-label": label}, "matchRules": ["SyntheticExclusiveGroup$"]})


def rejected(edition, status, body=None):
    try:
        c.original_api("api/v3/movieedition/" + str(edition["id"]) + "/remove", expected(edition) if body is None else body)
    except urllib.error.HTTPError as error:
        assert error.code == status, (error.code, error.read().decode())
    else:
        raise AssertionError("Unsafe unused-target removal was accepted")


def run(postgres):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    media = p.snapshot()
    original = p.rows("MovieEditions")
    target = create("Synthetic Unused Collector")
    assert not target["monitored"] and not target["isPrimary"] and not target.get("movieFileId"), target
    assert target["attributes"]["cut"] == "theatrical" and target["attributes"]["stereoscopic"] == "3d" and target["attributes"]["framing"] == "imax", target
    rejected(target, 409, dict(expected(target), expectedName="Synthetic Stale Name"))
    missing = expected(target)
    del missing["expectedMovieFileId"]
    rejected(target, 400, missing)
    for existing in p.api("api/v3/movieedition?movieId=1"):
        if existing["id"] != target["id"]:
            rejected(existing, 409)
    assert p.snapshot() == media
    c.configure_browser_auth()
    subprocess.run(["node", "/src/docs/development/verify-edition-unused-removal-browser.cjs", str(target["id"])], check=True)
    assert p.rows("MovieEditions") == original and p.snapshot() == media
    assert p.api("api/v3/movie/1")["editionCount"] == 2
    print("PASS: real API creation/removal preserves both original combined editions, profiles, ownership, primary projection and video/subtitle bytes/IDs", flush=True)

    retained = [create("Synthetic Saved " + label) for label in ("Prepared", "Settled", "History")]
    p.stop()
    with p.database() as db:
        for index, edition in enumerate(retained[:2]):
            db.execute('''INSERT INTO "MovieEditionSubmissions" ("OperationId","MovieId","MovieEditionId","EditionRuleRevision",
                "EditionName","EditionAttributes","QualityProfileId","MovieFileId","MoviePath","DownloadClientId","DownloadClientName",
                "Release","ParsedMovieInfo","DownloadId","State","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,NULL,?,?,?,?,?,?,?,?,?)''',
                ("00000000-0000-4000-8000-00000000004" + str(index + 1), 1, edition["id"], edition["ruleRevision"], edition["name"],
                 json.dumps(edition["attributes"]), edition["qualityProfileId"], str(p.MOVIE), 41, "Synthetic Client",
                 json.dumps({"title": "Synthetic.Lunar.Voyage.2026-SyntheticGroup"}), '{}', None if index == 0 else "synthetic-settled",
                 0 if index == 0 else 3, "2026-11-13T00:00:00Z", "2026-11-13T00:00:00Z"))
        edition = retained[2]
        db.execute('''INSERT INTO "History" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Quality","Date","EventType","Data","Languages")
            VALUES (1,?,?,?,?,?,8,?,'[1]')''', (edition["id"], edition["ruleRevision"], "Synthetic.Lunar.Voyage.2026-SyntheticGroup",
                json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}}), "2026-11-13T00:00:00Z",
                json.dumps({"movieEditionName": edition["name"]})))
    saved = {table: p.rows(table) for table in ("MovieEditions", "MovieEditionSubmissions", "History")}
    for restart in range(2):
        p.start()
        for edition in retained:
            rejected(edition, 409)
        p.command("Housekeeping")
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        assert p.snapshot() == media
        assert all(p.rows(table) == rows for table, rows in saved.items())
        assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
        p.stop()
    print("PASS: prepared intent, settled intent and saved rename history each block unused-target removal across two restarts/housekeeping; all evidence and both editions remain intact without acquisition or retry", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise ValueError("Only --postgres is supported")
        run("--postgres" in sys.argv[1:])
    finally:
        p.cleanup()
