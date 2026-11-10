"""Run inside the disposable SDK container, with external networking disabled.

Requires a built /src/_output/net10.0 application, Python 3, ffmpeg and ffprobe.
Seeds synthetic movie/edition definitions while Radarr is stopped (edition CRUD
is a later phase); all file handling is exercised through the running API.
Pass --postgres to use the dedicated radarr-edition-postgres Docker alias with
python3-psycopg2 installed; each run creates and drops its own main/log databases.
"""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request


APP = Path("/src/_output/net10.0")
RESULTS = Path("/src/_temp/edition-test-results")
KEY = "11112222333344445555666677778888"
ROOT = Path(tempfile.mkdtemp(prefix="radarr-phase3-", dir="/tmp"))
CONFIG = ROOT / "config"
LIBRARY = ROOT / "movies"
MOVIE = LIBRARY / "Synthetic Lunar Voyage (2026)"
PROCESS = None
LOG = None
sys.dont_write_bytecode = True
_database_spec = importlib.util.spec_from_file_location("edition_test_database", Path(__file__).with_name("edition-test-database.py"))
_database_module = importlib.util.module_from_spec(_database_spec)
_database_spec.loader.exec_module(_database_module)
DATABASE = _database_module.TestDatabase(CONFIG, lambda: PROCESS is None or PROCESS.poll() is not None)
database = DATABASE.connect


def cleanup():
    stop()
    try:
        DATABASE.cleanup()
    finally:
        shutil.rmtree(ROOT)


def api(path, body=None, method=None):
    request = urllib.request.Request(
        "http://127.0.0.1:17878/" + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={"X-Api-Key": KEY, "Content-Type": "application/json"},
        method=method,
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        data = response.read()
        return json.loads(data) if data else None


def start():
    global PROCESS, LOG
    LOG = open(RESULTS / "phase3-runtime.log", "a")
    env = {k: v for k, v in os.environ.items() if not k.lower().startswith("radarr__")}
    env.update(DATABASE.environment())
    PROCESS = subprocess.Popen(
        ["dotnet", str(APP / "Radarr.dll"), "--nobrowser", "--data=" + str(CONFIG)],
        cwd=APP, env=env, stdout=LOG, stderr=subprocess.STDOUT,
    )
    for _ in range(90):
        if PROCESS.poll() is not None:
            raise RuntimeError("Radarr exited; see phase3-runtime.log")
        try:
            status = api("api/v3/system/status")
            expected = "postgresql" if DATABASE.postgres else "sqlite"
            assert status["databaseType"].casefold() == expected, status
            if DATABASE.postgres:
                assert not (CONFIG / "radarr.db").exists(), "PostgreSQL smoke test must not use a SQLite main database"
            return
        except (OSError, ValueError):
            time.sleep(1)
    raise RuntimeError("Radarr did not become ready")


def stop():
    global PROCESS, LOG
    if PROCESS is not None:
        PROCESS.terminate()
        try:
            PROCESS.wait(timeout=20)
        except subprocess.TimeoutExpired:
            PROCESS.kill()
            PROCESS.wait()
        PROCESS = None
    if LOG is not None:
        LOG.close()
        LOG = None


def command(name, **fields):
    item = api("api/v3/command", dict(name=name, **fields))
    for _ in range(90):
        item = api("api/v3/command/" + str(item["id"]))
        if item["status"] == "completed":
            return item
        if item["status"] in ("failed", "aborted"):
            raise AssertionError(item)
        time.sleep(0.5)
    raise RuntimeError("Command timed out: " + name)


def rows(table):
    return DATABASE.rows(table)


def snapshot():
    files = [file for file in rows("MovieFiles") if file["MovieId"] == 1]
    extras = [extra for extra in rows("SubtitleFiles") if extra["MovieId"] == 1]
    assert len(files) == 2, files
    assert len(extras) == 2, extras
    result = {}
    for file in files:
        extra = next(e for e in extras if e["MovieFileId"] == file["Id"])
        result[file["MovieEditionId"]] = (
            file["Id"], file["RelativePath"],
            hashlib.sha256((MOVIE / file["RelativePath"]).read_bytes()).hexdigest(),
            extra["Id"], extra["RelativePath"], (MOVIE / extra["RelativePath"]).read_text(),
        )
    editions = [edition for edition in rows("MovieEditions") if edition["MovieId"] == 1]
    assert {e["Id"]: e["MovieFileId"] for e in editions if e["MovieFileId"] is not None} == {k: v[0] for k, v in result.items()}
    assert json.loads(editions[0]["Attributes"]) == {"cut": "theatrical", "framing": "imax"}
    assert json.loads(editions[1]["Attributes"]) == {"cut": "directors-cut", "stereoscopic": "3d"}
    movie = api("api/v3/movie/1")
    assert movie["movieFile"]["id"] == result[1][0] and movie["hasFile"]
    return result


def source(marker, colour):
    folder = ROOT / "downloads" / marker
    folder.mkdir(parents=True)
    path = folder / ("Synthetic.Lunar.Voyage.2026." + marker + "-SyntheticGroup.mkv")
    subprocess.run([
        "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=" + colour + ":s=160x90:r=24",
        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "3",
        "-c:v", "mpeg4", "-c:a", "aac", str(path),
    ], check=True)
    path.with_suffix(".en.srt").write_text("1\n00:00:00,000 --> 00:00:02,000\nSynthetic " + marker + "\n")
    return path


def manual(edition, marker, colour):
    path = source(marker, colour)
    command("ManualImport", importMode="copy", files=[{
        "movieId": 1, "movieEditionId": edition, "path": str(path),
        "quality": {"quality": {"id": 7, "name": "Bluray-1080p", "source": "bluray", "resolution": 1080}, "revision": {"version": 1, "real": 0, "isRepack": False}},
        "languages": [{"id": 1, "name": "English"}], "releaseGroup": "SyntheticGroup", "indexerFlags": 0,
    }])


def run():
    (RESULTS / "phase3-runtime.log").write_text("")
    CONFIG.mkdir()
    MOVIE.mkdir(parents=True)
    (ROOT / "recycle").mkdir()
    (CONFIG / "config.xml").write_text(
        "<Config><BindAddress>127.0.0.1</BindAddress><Port>17878</Port>"
        "<ApiKey>" + KEY + "</ApiKey><AuthenticationMethod>None</AuthenticationMethod>"
        "<AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>"
        "<LaunchBrowser>False</LaunchBrowser><AnalyticsEnabled>False</AnalyticsEnabled></Config>"
    )
    # Use the container's installed ffprobe with its matching shared libraries.
    if not (APP / "ffprobe").exists():
        (APP / "ffprobe").symlink_to(shutil.which("ffprobe"))
    start()
    stop()
    with database() as db:
        db.execute("""INSERT INTO "MovieMetadata" ("Id","TmdbId","Images","Title","CleanTitle","OriginalTitle","CleanOriginalTitle","OriginalLanguage","Status","Runtime","Recommendations","Year","Genres","Keywords","Ratings") VALUES (1,-1000001,'[]','Synthetic Lunar Voyage','syntheticlunarvoyage','Synthetic Lunar Voyage','syntheticlunarvoyage',1,3,100,'[]',2026,'[]','[]','{}')""")
        db.execute("""INSERT INTO "Movies" ("Id","Path","Monitored","QualityProfileId","MovieFileId","MinimumAvailability","MovieMetadataId","Tags","PrimaryEditionId") VALUES (1,?,FALSE,1,0,3,1,'[]',1)""", (str(MOVIE),))
        for eid, key, name, attrs in [
            (1, "theatrical-imax", "Theatrical IMAX", {"cut": "theatrical", "framing": "imax"}),
            (2, "directors-3d", "Directors Cut 3D", {"cut": "directors-cut", "stereoscopic": "3d"}),
        ]:
            db.execute("""INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes") VALUES (?,1,?,?,FALSE,1,?)""", (eid, key, name, json.dumps(attrs)))
        for key, value in {"ImportExtraFiles": "True", "ExtraFileExtensions": "srt", "CopyUsingHardlinks": "False", "RecycleBin": str(ROOT / "recycle")}.items():
            db.execute("""DELETE FROM "Config" WHERE "Key"=?""", (key.lower(),))
            db.execute("""INSERT INTO "Config" ("Key","Value") VALUES (?,?)""", (key.lower(), value))
    start()
    naming = api("api/v3/config/naming")
    naming.update(renameMovies=True, standardMovieFormat="{Movie Title} ({Release Year}) [{Managed Edition}]")
    api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    manual(1, "Theatrical.IMAX", "red")
    manual(2, "Directors.Cut.3D", "blue")
    snapshot()
    print("PASS: two explicit manual imports and file-specific subtitles", flush=True)
    command("RescanMovie", movieId=1)
    naming["standardMovieFormat"] += " renamed"
    api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    command("RenameMovie", movieIds=[1])
    before = snapshot()
    assert all("renamed" in f[1] and "renamed" in f[4] for f in before.values())
    stop()
    start()
    command("RescanMovie", movieId=1)
    command("Housekeeping")
    assert snapshot() == before
    print("PASS: scan, rename, actual process restart, housekeeping, attributes and primary API projection", flush=True)
    for target, colour in [(1, "green"), (2, "yellow")]:
        before = snapshot()
        manual(target, "Upgrade" + str(target), colour)
        after = snapshot()
        other = 3 - target
        assert after[other] == before[other]
        assert after[target][0] != before[target][0] and after[target][2] != before[target][2]
        assert after[target][5] != before[target][5]
        print("PASS: edition " + str(target) + " upgrade preserves other file and subtitle bytes/IDs", flush=True)
    before = snapshot()
    stop()
    start()
    command("RescanMovie", movieId=1)
    assert snapshot() == before
    print("PASS: both upgraded editions survive a second process restart and scan", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["--postgres"]:
            DATABASE.enable_postgres()
        elif sys.argv[1:]:
            raise ValueError("Only --postgres is supported")
        run()
    finally:
        cleanup()
