"""Exercise manual-only edition retirement in the disposable offline Docker runner.

Uses synthetic media, its own configuration/databases and built production UI.
Pass --postgres for the isolated test server; never point at a real library.
"""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import urllib.error

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("unused_removal", Path(__file__).with_name("verify-edition-unused-removal.py"))
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)
p = u.p
c = u.c


def retire(edition, status=None, body=None):
    try:
        result = c.original_api("api/v3/movieedition/" + str(edition["id"]) + "/retire", u.expected(edition) if body is None else body)
    except urllib.error.HTTPError as error:
        assert status is not None and error.code == status, (error.code, error.read().decode())
    else:
        assert status is None, ("Unsafe retirement accepted", result)
        return result


def run(postgres):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    original = p.snapshot()
    target = u.create("Synthetic Retired Collector")
    p.manual(target["id"], "Collector.3D.IMAX", "purple")
    target = p.api("api/v3/movieedition/" + str(target["id"]))
    assert target["movieFileId"] and not target["monitored"]
    retire(target, 409)
    missing = u.expected(target)
    del missing["expectedMovieFileId"]
    retire(target, 400, missing)

    # Rename the third file only, preserving both baseline editions' exact paths.
    naming = p.api("api/v3/config/naming")
    naming["standardMovieFormat"] += " collector-renamed"
    p.api("api/v3/config/naming", naming, "PUT")
    p.command("RenameFiles", movieId=1, files=[target["movieFileId"]])
    files = p.rows("MovieFiles")
    third = next(file for file in files if file["Id"] == target["movieFileId"])
    assert "collector-renamed" in third["RelativePath"], third
    assert all((p.MOVIE / value[1]).exists() for value in original.values())
    p.api("api/v3/moviefile/" + str(target["movieFileId"]), method="DELETE")
    assert not (p.MOVIE / third["RelativePath"]).exists()
    target = p.api("api/v3/movieedition/" + str(target["id"]))
    assert not target.get("movieFileId") and p.snapshot() == original
    history = [row for row in p.rows("History") if row["MovieEditionId"] == target["id"]]
    assert {row["EventType"] for row in history} == {3, 6, 8}, history
    assert all(json.loads(row["Data"])["movieEditionName"] == target["name"] and row["EditionRuleRevision"] > 0 for row in history)
    assert all(row.get("EditionOperationId") is None and not row.get("DownloadId") for row in history)
    u.rejected(target, 409)  # Strict unused-target compatibility route remains strict.
    retire(target, 409, dict(u.expected(target), expectedName="Synthetic Stale Collector"))

    c.configure_browser_auth()
    subprocess.run(["node", "/src/docs/development/verify-edition-retirement-browser.cjs", str(target["id"])], check=True)
    assert p.snapshot() == original
    assert [row for row in p.rows("History") if row["MovieEditionId"] == target["id"]] == history
    assert len(p.api("api/v3/movieedition?movieId=1")) == 2
    # Restore the previous naming before scan/rename so the two retained files stay exact.
    naming["standardMovieFormat"] = naming["standardMovieFormat"].removesuffix(" collector-renamed")
    p.api("api/v3/config/naming", naming, "PUT")
    for _ in range(2):
        p.stop()
        p.start()
        p.command("Housekeeping")
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        assert p.snapshot() == original
        assert [row for row in p.rows("History") if row["MovieEditionId"] == target["id"]] == history
        api_history = p.api("api/v3/history/movie?movieId=1")
        retired_history = [row for row in api_history if row.get("movieEditionId") == target["id"]]
        assert len(retired_history) == 3 and all(row["data"]["movieEditionName"] == target["name"] for row in retired_history), retired_history
        assert all(row["movieEditionName"] == target["name"] and not row["editionContextValid"] and row.get("customFormatScore") is None and row.get("qualityProfileId") is None for row in retired_history), retired_history
        assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: real manual import, file-specific rename and deletion precede guarded retirement; original history IDs/names survive scan, rename, housekeeping and two process restarts", flush=True)
    print("PASS: browser cancel sends no write; stale confirmation conflicts; fresh retirement preserves both retained combined editions and all their video/subtitle bytes and IDs without acquisition", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise SystemExit("usage: verify-edition-retirement.py [--postgres]")
        run("--postgres" in sys.argv)
    finally:
        p.cleanup()
