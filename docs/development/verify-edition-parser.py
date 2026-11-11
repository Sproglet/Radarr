"""Edition-scoped parser previews against disposable SQLite/PostgreSQL data.

Run inside the externally offline SDK test runner with the same prerequisites as
verify-phase3.py. Uses only synthetic titles/groups and generated videos/subtitles.
"""

import importlib.util
import json
from pathlib import Path
import sys
import subprocess
import urllib.error
import urllib.parse

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p
TABLES = ["Movies", "MovieEditions", "MovieFiles", "SubtitleFiles", "History", "DownloadHistory", "PendingReleases"]


def read(title, score, status, eid=None, selected=None):
    before = {table: p.rows(table) for table in TABLES}
    query = {"title": title}
    if selected is not None:
        query["movieEditionId"] = selected
    result = p.api("api/v3/parse?" + urllib.parse.urlencode(query))
    assert result.get("customFormatScore") == score, result
    assert result["editionClassification"]["status"] == status, result
    assert result.get("movieEditionId") == eid and result["editionSelectionExplicit"] == (selected is not None), result
    if score is None:
        assert result.get("qualityProfileId") is None and result.get("movieEditionName") is None, result
    assert before == {table: p.rows(table) for table in TABLES}
    return result


def rejected(title, eid, code):
    before = {table: p.rows(table) for table in TABLES}
    try:
        c.original_api("api/v3/parse?" + urllib.parse.urlencode({"title": title, "movieEditionId": eid}))
    except urllib.error.HTTPError as error:
        assert error.code == code, (error.code, error.read().decode())
    else:
        raise AssertionError("Invalid parser target must reject")
    assert before == {table: p.rows(table) for table in TABLES}


def run(postgres, browser=False):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary,))
        db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
            VALUES (900,999999,'synthetic-foreign-parser','Synthetic Foreign Parser',FALSE,1,'{}')''')
    p.start()
    media = p.snapshot()
    imax = "Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup"
    directors = "Synthetic.Lunar.Voyage.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup"
    first = read(imax, 11, "matched", 1)
    second = read(directors, 77, "matched", 2)
    assert first["qualityProfileId"] == 1 and second["qualityProfileId"] == secondary
    assert first["movie"]["qualityProfileId"] == second["movie"]["qualityProfileId"] == 1
    read(directors, 11, "matched", 1, 1)
    read(imax, 77, "matched", 2, 2)
    for title in ["Synthetic.Lunar.Voyage.2026.1080p.BluRay-SyntheticGroup",
                  "Synthetic.Lunar.Voyage.2026.3D.1080p.BluRay-SyntheticGroup",
                  "Synthetic.Lunar.Voyage.2026.Criterion.1080p.BluRay-SyntheticGroup"]:
        read(title, None, "unconfigured")
    for selected in [None, 2]:
        title = "Synthetic.Unmapped.Comet.2044.1080p.BluRay-SyntheticGroup"
        result = read(title, None, "invalid", selected=selected)
        assert not result.get("movie"), result
        assert result.get("customFormats"), result
    read("???", None, "invalid")
    for eid, code in [(0, 400), (-1, 400), (999998, 404), (900, 404)]:
        rejected(directors, eid, code)
    print("PASS: parser auto/explicit previews use independent scores/profiles, retain the movie default, and expose unknown context without zero fallback or foreign/unmapped target assignment", flush=True)

    collector = p.api("api/v3/movieedition", {"movieId": 1, "name": "Synthetic Parser Collector 3D IMAX", "qualityProfileId": secondary,
        "attributes": {"stereoscopic": "3d", "framing": "imax", "branding": "Synthetic Parser Collector"}, "matchRules": ["SyntheticCollector"]})
    read("Synthetic.Lunar.Voyage.2026.SyntheticCollector.1080p.BluRay-SyntheticGroup", 77, "matched", collector["id"])
    duplicate = p.api("api/v3/movieedition", {"movieId": 1, "name": "Synthetic Parser Duplicate Directors 3D",
        "attributes": {"cut": "directors-cut", "stereoscopic": "3d"}})
    result = read(directors, None, "ambiguous")
    assert result["editionClassification"]["candidateEditionIds"] == [2, duplicate["id"]]
    read(directors, 77, "matched", 2, 2)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "MatchRules"=? WHERE "Id"=?', ('["SyntheticNeverMatches$"]', duplicate["id"]))
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=999999 WHERE "Id"=2')
    p.start()
    read(directors, None, "invalid")
    read(directors, None, "invalid", selected=2)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=?,"MovieFileId"=? WHERE "Id"=2', (secondary, media[1][0]))
    p.start()
    read(directors, None, "invalid", selected=2)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "MovieFileId"=? WHERE "Id"=2', (media[2][0],))
    p.start()
    read(directors, 77, "matched", 2)
    assert p.snapshot() == media
    if browser:
        c.configure_browser_auth()
        before = {table: p.rows(table) for table in TABLES}
        subprocess.run(["node", "/src/docs/development/verify-edition-parser-browser.cjs"], check=True)
        assert before == {table: p.rows(table) for table in TABLES}
        assert p.snapshot() == media
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: combined/custom rules, ambiguous targets, missing profiles and broken ownership stay diagnostic; valid independent scoring returns after repair/restart with media/history intact and no acquisition", flush=True)


if __name__ == "__main__":
    try:
        if any(arg not in {"--postgres", "--browser"} for arg in sys.argv[1:]) or len(set(sys.argv[1:])) != len(sys.argv[1:]):
            raise ValueError("Only --postgres and --browser are supported")
        run("--postgres" in sys.argv[1:], "--browser" in sys.argv[1:])
    finally:
        p.cleanup()
