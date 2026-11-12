"""Verify durable intent retention using disposable configuration and synthetic media.

Run in the isolated SDK runner; optional --postgres. Intent is seeded offline
because automatic acquisition and public journal/submission endpoints stay gated.
Repository preparation and evidence transitions are covered by database fixtures.
"""

import importlib.util
import json
from pathlib import Path
import sys
import urllib.error

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("phase3", Path(__file__).with_name("verify-phase3.py"))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def run(postgres):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    media = p.snapshot()
    p.stop()
    movie = p.rows("Movies")[0]
    edition = next(row for row in p.rows("MovieEditions") if row["Id"] == 2)
    attributes = edition["Attributes"]
    if not isinstance(attributes, str):
        attributes = json.dumps(attributes)
    release = {"title": "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup", "guid": "synthetic-journal-release"}
    with p.database() as db:
        db.execute('''INSERT INTO "MovieEditionSubmissions" ("OperationId","MovieId","MovieEditionId","EditionRuleRevision",
            "EditionName","EditionAttributes","QualityProfileId","MovieFileId","MoviePath","DownloadClientId","DownloadClientName",
            "Release","ParsedMovieInfo","DownloadId","State","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,NULL,0,?,?)''',
            ("00000000-0000-4000-8000-000000000041", movie["Id"], edition["Id"], edition["RuleRevision"], edition["Name"], attributes,
             edition["QualityProfileId"], edition["MovieFileId"], movie["Path"], 41, "Synthetic Client", json.dumps(release), '{}',
             "2026-11-12T12:00:00Z", "2026-11-12T12:00:00Z"))
    saved = p.rows("MovieEditionSubmissions")
    for _ in range(2):
        p.start()
        p.command("Housekeeping")
        assert p.rows("MovieEditionSubmissions") == saved and p.snapshot() == media
        for delete_files in (False, True):
            try:
                p.api("api/v3/movie/1?deleteFiles=" + str(delete_files).lower(), method="DELETE")
            except urllib.error.HTTPError as error:
                assert error.code == 409, (error.code, error.read().decode())
                assert "submission intent" in error.read().decode()
            else:
                raise AssertionError("Movie deletion bypassed unresolved submission intent")
            assert p.snapshot() == media and p.rows("MovieEditionSubmissions") == saved
            assert len(p.api("api/v3/movieedition?movieId=1")) == 2
        p.stop()
    print("PASS: prepared intent survives housekeeping and two process restarts with its original operation and edition snapshot", flush=True)
    print("PASS: both movie-deletion modes return 409 before removing either edition's video/subtitle bytes or IDs", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise ValueError("Only --postgres is supported")
        run(sys.argv[1:] == ["--postgres"])
    finally:
        p.cleanup()
