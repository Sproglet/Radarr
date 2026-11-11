"""Exercise guarded edition definition edits with disposable data and fake jobs.

Use the same externally offline SDK runner as verify-phase3.py; --postgres
uses only freshly created databases on the dedicated test-server alias.
"""

import importlib.util
import json
from pathlib import Path
import sys
import subprocess
import urllib.error

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p


def request(eid, **changes):
    edition = p.api("api/v3/movieedition/" + str(eid))
    body = c.monitoring_request(eid, False)
    body.pop("monitored")
    return {
        **body, "name": edition["name"], "attributes": edition["attributes"], "matchRules": edition["matchRules"], **changes}


def endpoint(eid, preview=False):
    return "api/v3/movieedition/" + str(eid) + "/definition" + ("/preview" if preview else "")


def rejected(eid, body, code, preview=False):
    try:
        c.original_api(endpoint(eid, preview), body)
    except urllib.error.HTTPError as error:
        assert error.code == code, (error.code, error.read().decode(), body)
    else:
        raise AssertionError("Invalid or stale definitions must reject")


def run(postgres, browser=False):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    empty = p.api("api/v3/movieedition", {"movieId": 1, "name": "Synthetic Definition Collector",
        "attributes": {"branding": "Synthetic Definition Collector"}})
    clients = [c.SyntheticSab(1), c.SyntheticSab(2)]
    c.SERVERS.extend(clients)
    p.stop()
    c.seed(clients, secondary)
    p.start()
    if browser:
        before_browser = {table: p.rows(table) for table in ["Movies", "MovieFiles", "SubtitleFiles", "History", "DownloadHistory"]}
        before_browser_media = p.snapshot()
        c.configure_browser_auth()
        subprocess.run(["node", "/src/docs/development/verify-edition-definitions-browser.cjs", str(empty["id"]), str(p.MOVIE)], check=True)
        assert before_browser == {table: p.rows(table) for table in before_browser}
        assert p.snapshot() == before_browser_media
    initial_queue = c.queue([1, 2])
    original = {table: p.rows(table) for table in ["Movies", "MovieFiles", "SubtitleFiles", "History", "DownloadHistory"]}
    definitions = p.rows("MovieEditions")
    media = p.snapshot()
    body = request(2, name="Synthetic Directors 3D Collector",
        attributes={"cut": "directors-cut", "stereoscopic": "3d", "framing": "imax", "branding": "Synthetic Definition Collector"},
        matchRules=["SyntheticNewRuleThatDoesNotMatchExistingJobs$"])
    invalid = [(2, dict(body, expectedMovieMonitored=True), 409), (2, dict(body, expectedEditionMonitored=True), 409),
        (2, dict(body, expectedMovieFileId=None), 409), (2, dict(body, expectedQualityProfileId=999999), 409),
        (2, dict(body, expectedRuleRevision=999999), 409), (2, dict(body, expectedName="Synthetic Stale"), 409),
        (2, dict(body, movieId=999999), 404), (999999, body, 404), (0, body, 400),
        (2, dict(body, name="Synthetic\0Cut"), 400), (2, dict(body, name=" " * 3), 400),
        (2, dict(body, matchRules=["["]), 400), (2, dict(body, attributes=None), 400),
        (2, dict(body, matchRules=None), 400), (2, dict(body, attributes={str(i): "Synthetic" for i in range(33)}), 400)]
    for key in body:
        omitted = dict(body)
        del omitted[key]
        invalid.append((2, omitted, 400))
    for eid, candidate, code in invalid:
        for preview in [True, False]:
            rejected(eid, candidate, code, preview)
            assert original == {table: p.rows(table) for table in original} and definitions == p.rows("MovieEditions")
    preview = p.api(endpoint(2, True), body)
    assert preview["editingEnabled"] is True and preview["creationEnabled"] is False and len(preview["samples"]) == 3
    assert original == {table: p.rows(table) for table in original} and definitions == p.rows("MovieEditions")
    relative = next(sample["relativePath"] for sample in preview["samples"] if sample.get("movieEditionId") == 2)
    for extension in [".mp4", ".en.srt"]:
        conflict = (p.MOVIE / relative).with_suffix(extension)
        assert conflict.resolve().is_relative_to(p.MOVIE.resolve()) and not conflict.exists()
        conflict.write_text("Synthetic reserved destination")
        try:
            assert p.api(endpoint(2, True), body)["editingEnabled"] is False
            rejected(2, body, 409)
            assert original == {table: p.rows(table) for table in original} and definitions == p.rows("MovieEditions")
        finally:
            conflict.unlink()
    for name in ["Theatrical IMAX", "Theatrical IMAX<>"]:
        candidate = dict(body, name=name)
        assert p.api(endpoint(2, True), candidate)["editingEnabled"] is False
        rejected(2, candidate, 409)
    naming = p.api("api/v3/config/naming")
    changed = dict(naming, standardMovieFormat="{Movie Title} ({Release Year})")
    p.api("api/v3/config/naming/" + str(naming["id"]), changed, "PUT")
    rejected(2, body, 409)
    p.api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    print("PASS: complete fresh state is required for definition previews/saves; invalid rules/names/attributes, sanitized duplicate names, changed naming and unassigned video/sidecar collisions reject without mutation", flush=True)

    edited = p.api(endpoint(2), body)
    expected_attributes = {1: {"cut": "theatrical", "framing": "imax"}, 2: body["attributes"]}
    assert edited["ruleRevision"] == body["expectedRuleRevision"] + 1 and edited["name"] == body["name"]
    assert p.snapshot(expected_attributes) == media
    rejected(2, body, 409)
    same = request(2)
    assert p.api(endpoint(2), same)["ruleRevision"] == edited["ruleRevision"]
    primary_request = request(1, name="Synthetic Theatrical IMAX Primary")
    assert p.api(endpoint(1), primary_request)["ruleRevision"] == primary_request["expectedRuleRevision"]
    empty_request = request(empty["id"], name="Synthetic Empty 3D IMAX Collector",
        attributes={"stereoscopic": "3d", "framing": "imax", "branding": "Synthetic Empty / Collector"}, matchRules=["SyntheticEmptyCollector$"])
    empty_edited = p.api(endpoint(empty["id"]), empty_request)
    assert empty_edited["attributes"] == {**empty_request["attributes"], "cut": "theatrical"}
    for edition in p.rows("MovieEditions"):
        before = next(row for row in definitions if row["Id"] == edition["Id"])
        for field in ["EditionKey", "MovieId", "QualityProfileId", "Monitored", "MovieFileId", "LastSearchTime"]:
            assert edition[field] == before[field], (field, edition, before)
    assert original == {table: p.rows(table) for table in original}
    assert p.snapshot(expected_attributes) == media
    current_queue = c.queue([1, 2])
    assert {key: row["id"] for key, row in current_queue.items()} == {key: row["id"] for key, row in initial_queue.items()}
    assert original == {table: p.rows(table) for table in original}
    p.stop()
    p.start()
    c.queue([1, 2])
    assert p.snapshot(expected_attributes) == media and original == {table: p.rows(table) for table in original}
    print("PASS: primary/secondary/empty definitions edit independently, default absent cuts to Theatrical with combined/custom labels, increment classification revisions once and preserve saved jobs/history/preferences/file bytes and IDs through restart", flush=True)

    p.command("RenameMovie", movieIds=[1])
    renamed = p.snapshot(expected_attributes)
    for eid in [1, 2]:
        assert (renamed[eid][0], renamed[eid][2], renamed[eid][3], renamed[eid][5]) == (media[eid][0], media[eid][2], media[eid][3], media[eid][5])
    assert "Synthetic Directors 3D Collector" in renamed[2][1] and "Synthetic Theatrical IMAX Primary" in renamed[1][1]
    for target, colour in [(1, "white"), (2, "purple")]:
        previous = p.snapshot(expected_attributes)
        p.manual(target, "SyntheticDefinitionUpgrade" + str(target), colour)
        current = p.snapshot(expected_attributes)
        assert current[3 - target] == previous[3 - target] and current[target][0] != previous[target][0]
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        p.stop()
        p.start()
        assert p.snapshot(expected_attributes) == current
    assert all(not client.deleted and not client.rejected for client in clients)
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: explicit rename uses edited labels without changing ownership, and both subsequent manual upgrades preserve the other edition through scan/rename/process restart without acquisition", flush=True)


if __name__ == "__main__":
    try:
        if any(arg not in {"--postgres", "--browser"} for arg in sys.argv[1:]) or len(set(sys.argv[1:])) != len(sys.argv[1:]):
            raise ValueError("Only --postgres and --browser are supported")
        run("--postgres" in sys.argv[1:], "--browser" in sys.argv[1:])
    finally:
        p.stop()
        try:
            for client in c.SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
