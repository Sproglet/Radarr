"""Offline SQLite/PostgreSQL process-restart and HTTP queue regression with synthetic SAB APIs.

Run only inside a disposable SDK container with this checkout mounted at /src,
a complete net10.0 application build, Python/ffmpeg, and no external network.
No real client is contacted and acquisition requests to the fake APIs fail.
The manual-ui scenario also requires a built UI copied into the application UI
folder, Node, Playwright Core at /src/_temp/edition-browser-tools and its bundled
headless Chromium. Install test tools before disconnecting external networking.
Append --postgres to a scenario to use unique disposable main/log databases on
the dedicated radarr-edition-postgres alias (python3-psycopg2 required).
"""

import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import threading
import urllib.error

sys.dont_write_bytecode = True
from urllib.parse import parse_qs, urlsplit

spec = importlib.util.spec_from_file_location("phase3", Path(__file__).with_name("verify-phase3.py"))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
original_api = p.api


def checked_api(*args, **kwargs):
    try:
        return original_api(*args, **kwargs)
    except urllib.error.HTTPError as error:
        raise AssertionError((error.code, error.read().decode())) from error


p.api = checked_api
SHARED = "synthetic-sab-shared-id"
SERVERS = []


class SyntheticSab:
    def __init__(self, number):
        self.number = number
        self.client_id = 40 + number
        self.deleted = False
        self.mode = "downloading"
        self.output = p.ROOT / "downloads"
        self.requests = []
        self.rejected = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                owner.rejected.append("POST")
                self.send_error(409, "Synthetic client does not acquire releases")

            def do_GET(self):
                args = parse_qs(urlsplit(self.path).query)
                mode = args.get("mode", [""])[0]
                name = args.get("name", [""])[0]
                owner.requests.append((mode, name))
                if mode in ("queue", "history") and name == "delete":
                    assert args.get("value") == [SHARED], args
                    owner.deleted = True
                    result = {"status": True}
                elif mode == "queue":
                    slots = [] if owner.deleted or owner.mode != "downloading" else [{
                        "nzo_id": SHARED, "filename": "opaque-synthetic", "cat": "movies",
                        "status": "Downloading", "mb": "100", "mbleft": "75", "timeleft": "00:10:00", "priority": "Normal",
                    }]
                    result = {"queue": {"paused": False, "slots": slots}}
                elif mode == "history":
                    slots = [] if owner.deleted or owner.mode == "downloading" else [{
                        "nzo_id": SHARED, "name": "opaque-synthetic", "category": "movies", "bytes": 104857600,
                        "status": {"completed": "Completed", "failed": "Failed", "extracting": "Extracting"}[owner.mode],
                        "fail_message": "Synthetic failed download" if owner.mode == "failed" else "", "storage": str(owner.output),
                    }]
                    result = {"history": {"slots": slots, "noofslots": len(slots)}}
                elif mode == "version":
                    result = {"version": "4.5.0"}
                elif mode == "get_config":
                    result = {"config": {"misc": {"complete_dir": str(p.ROOT / "downloads"), "download_dir": str(p.ROOT / "downloads")},
                                         "categories": [{"name": "movies", "dir": ""}], "sorters": []}}
                elif mode == "fullstatus":
                    result = {"status": {"my_home": str(p.ROOT)}}
                else:
                    owner.rejected.append((mode, name))
                    self.send_error(409, "Unexpected synthetic client operation")
                    return
                body = json.dumps(result).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


def seed(clients, secondary_profile):
    """Only seed the unique temporary configuration, while Radarr is stopped."""
    with p.database() as db:
        db.execute("""UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2""", (secondary_profile,))
        for client, edition, label in zip(clients, [1, 2], ["Theatrical.IMAX", "Directors.Cut.3D"]):
            settings = {"host": "127.0.0.1", "port": client.port, "apiKey": "synthetic-api-key", "movieCategory": "movies"}
            db.execute("""INSERT INTO "DownloadClients"
                ("Id","Enable","Name","Implementation","Settings","ConfigContract","Priority","RemoveCompletedDownloads","RemoveFailedDownloads","Tags")
                VALUES (?,TRUE,?,'Sabnzbd',?,'SabnzbdSettings',1,FALSE,FALSE,'[]')""",
                (client.client_id, "Synthetic SAB " + str(client.number), json.dumps(settings)))
            title = "Synthetic.Lunar.Voyage.2026." + label + ".1080p.BluRay-SyntheticGroup"
            edition_row = db.execute("""SELECT "Name","RuleRevision" FROM "MovieEditions" WHERE "Id"=?""", (edition,)).fetchone()
            data = {"downloadClientId": str(client.client_id), "movieEditionName": edition_row[0], "indexer": "Synthetic Indexer",
                    "publishedDate": "2026-11-09T00:00:00Z", "size": "104857600", "protocol": "1", "releaseSource": "Rss"}
            release = {"title": title, "guid": "synthetic-guid-" + str(edition), "indexer": "Synthetic Indexer", "indexerId": 77,
                       "size": 104857600, "downloadProtocol": "usenet", "publishDate": "2026-11-09T00:00:00Z"}
            quality = json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}})
            db.execute("""INSERT INTO "History" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Quality","Date","EventType","Data","Languages","DownloadId")
                VALUES (1,?,?,?,?,?,1,?,'[1]',?)""",
                (edition, edition_row[1], title, quality, "2026-11-09T00:00:00Z", json.dumps(data), SHARED))
            db.execute("""INSERT INTO "DownloadHistory" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Date","EventType","DownloadId","Protocol","IndexerId","DownloadClientId","Release","Data")
                VALUES (1,?,?,?,'2026-11-09T00:00:00Z',1,?,1,77,?,?,?)""",
                (edition, edition_row[1], title, SHARED, client.client_id, json.dumps(release), json.dumps(data)))


def queue(expected):
    p.command("RefreshMonitoredDownloads")
    result = p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"]
    rows = [r for r in result if r["downloadId"] == SHARED]
    assert {r["downloadClientId"] for r in rows} == {40 + slot for slot in expected}, rows
    for row in rows:
        assert row["movieEditionId"] == row["downloadClientId"] - 40, row
        assert row["trackedDownloadState"] == "downloading", row
        assert row["movieId"] == 1, row
        assert row["customFormatScore"] == row["editionCustomFormatScore"] == {41: 11, 42: 77}[row["downloadClientId"]], row
    return {r["downloadClientId"] - 40: r for r in rows}


def completed_source(client):
    label = {1: "Theatrical.IMAX", 2: "Directors.Cut.3D"}[client.number]
    client.output = p.ROOT / "downloads" / ("SyntheticCompleted" + str(client.number))
    client.output.mkdir()
    path = client.output / ("Synthetic.Lunar.Voyage.2026." + label + ".1080p.BluRay-SyntheticGroup.mkv")
    subprocess.run(["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=cyan:s=160x90:r=1", "-f", "lavfi", "-i",
        "anullsrc=r=8000:cl=mono", "-t", "6001", "-c:v", "mpeg4", "-c:a", "aac", "-metadata:s:a:0", "language=eng", str(path)], check=True)
    path.with_suffix(".en.srt").write_text("1\n00:00:00,000 --> 00:00:02,000\nSynthetic completed client " + str(client.number) + "\n")
    client.mode = "completed"


def verify_completions(clients, before):
    for client in clients:
        previous = p.snapshot()
        completed_source(client)
        p.command("RefreshMonitoredDownloads")
        p.command("ProcessMonitoredDownloads")
        current = p.snapshot()
        target = client.number
        other = 3 - target
        assert current[target][0] != previous[target][0], p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")
        assert current[other] == previous[other]
        assert current[target][5] != previous[target][5]
        p.stop()
        p.start()
        queue([2] if target == 1 else [])
        assert p.snapshot() == current
        print("PASS: completed HTTP client " + str(target) + " upgrades only its edition and subtitle, surviving process restart", flush=True)
    assert all(not c.deleted and not c.rejected for c in clients)
    print("PASS: both client-driven upgrades completed without acquisition or client deletion", flush=True)


def expect_bad_request(path, data=None):
    try:
        original_api(path, data)
    except urllib.error.HTTPError as error:
        assert error.code == 400, (error.code, error.read().decode())
    else:
        raise AssertionError("Expected HTTP 400: " + path)


def verify_manual(clients):
    before = p.snapshot()
    files = []
    for client in clients:
        path = p.source("SyntheticManualClient" + str(client.number), {1: "yellow", 2: "purple"}[client.number])
        client.output = path.parent
        client.mode = "extracting"
        files.append({"path": str(path), "downloadId": SHARED, "downloadClientId": client.client_id,
            "movieId": 1, "movieEditionId": client.number, "quality": {"quality": {"id": 7}, "revision": {"version": 1}},
            "languages": [{"id": 1}], "releaseGroup": "SyntheticGroup", "indexerFlags": 0})
    queue([1, 2])
    expect_bad_request("api/v3/manualimport?downloadId=" + SHARED)
    expect_bad_request("api/v3/manualimport?downloadClientId=41&movieId=1")
    expect_bad_request("api/v3/manualimport?downloadId=" + SHARED + "&downloadClientId=999")
    expect_bad_request("api/v3/manualimport", [dict(files[0], downloadClientId=None)])
    expect_bad_request("api/v3/manualimport", [dict(files[0], downloadClientId=999)])
    for file in files:
        preview = p.api("api/v3/manualimport?downloadId=" + SHARED + "&downloadClientId=" + str(file["downloadClientId"]))
        assert len(preview) == 1, preview
        assert preview[0]["movieEditionId"] == file["movieEditionId"] and preview[0]["downloadClientId"] == file["downloadClientId"], preview
        assert preview[0]["customFormatScore"] == {41: 11, 42: 77}[file["downloadClientId"]], preview
        processed = p.api("api/v3/manualimport", [file])[0]
        assert processed["movieEditionId"] == file["movieEditionId"] and processed["downloadClientId"] == file["downloadClientId"], processed
    wrong = dict(files[1], movieEditionId=1)
    processed = p.api("api/v3/manualimport", [wrong])[0]
    assert any("saved assignment" in r["reason"] for r in processed["rejections"]) and not processed["editionAssignmentValid"], processed
    try:
        p.command("ManualImport", importMode="copy", files=[files[0], wrong])
    except AssertionError as error:
        assert "failed" in str(error), error
    else:
        raise AssertionError("Mismatched saved assignment must reject batch")
    assert p.snapshot() == before
    assert not [r for r in p.rows("DownloadHistory") if r["EventType"] == 2]
    print("PASS: client-qualified previews/reprocessing stay assigned; ambiguous IDs and a mismatched batch reject before replacement", flush=True)
    for file in files:
        previous = p.snapshot()
        p.command("ManualImport", importMode="copy", files=[file])
        current = p.snapshot()
        target = file["movieEditionId"]
        assert current[target][0] != previous[target][0] and current[target][5] != previous[target][5]
        assert current[3 - target] == previous[3 - target]
        terminal = [r for r in p.rows("DownloadHistory") if r["EventType"] == 2]
        assert {(r["DownloadClientId"], r["MovieEditionId"]) for r in terminal} == ({(41, 1)} if target == 1 else {(41, 1), (42, 2)}), terminal
        p.stop()
        p.start()
        queue([2] if target == 1 else [])
        assert p.snapshot() == current
    assert all(not c.deleted and not c.rejected for c in clients)
    print("PASS: both same-ID manual imports replace only their saved edition, settle the correct client and survive restart", flush=True)


def configure_browser_auth():
    assert (p.APP / "UI" / "index.html").read_bytes() == Path("/src/_output/UI/index.html").read_bytes(), "Copy the current UI contents into the application UI folder before browser verification"
    host = p.api("api/v3/config/host")
    host.update(authenticationMethod="forms", authenticationRequired="disabledForLocalAddresses", allowedHosts="127.0.0.1,localhost",
                username="synthetic-browser-user", password="synthetic-browser-password",
                passwordConfirmation="synthetic-browser-password")
    p.api("api/v3/config/host", host, "PUT")
    p.stop()
    p.start()


def verify_browser_manual(clients, before):
    configure_browser_auth()
    for client in clients:
        label = {1: "Theatrical.IMAX", 2: "Directors.Cut.3D"}[client.number]
        path = p.source("SyntheticBrowser." + label + ".1080p.BluRay", {1: "yellow", 2: "purple"}[client.number])
        client.output = path.parent
        client.mode = "completed"
    p.command("RefreshMonitoredDownloads")
    p.command("ProcessMonitoredDownloads")
    records = p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"]
    assert len(records) == 2 and all(r["trackedDownloadStatus"] == "warning" and r["status"] == "completed" for r in records), records
    assert p.snapshot() == before
    for client in clients:
        previous = p.snapshot()
        subprocess.run(["node", "/src/docs/development/verify-edition-manual-browser.cjs", str(client.client_id)], check=True)
        current = p.snapshot()
        assert current[client.number][0] != previous[client.number][0]
        assert current[3 - client.number] == previous[3 - client.number]
        terminal = [r for r in p.rows("DownloadHistory") if r["EventType"] == 2]
        assert {(r["DownloadClientId"], r["MovieEditionId"]) for r in terminal} == ({(41, 1)} if client.number == 1 else {(41, 1), (42, 2)}), terminal
        p.stop()
        p.start()
        assert p.snapshot() == current
        p.command("RefreshMonitoredDownloads")
        remaining = p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"]
        assert {r["downloadClientId"] for r in remaining} == ({42} if client.number == 1 else set()), remaining
    assert all(not c.deleted and not c.rejected for c in clients)
    print("PASS: both browser-driven upgrades preserve the other edition/subtitle and settle only their client, across real restarts", flush=True)


def verify_edition_reads():
    before = p.snapshot()
    editions_before = p.rows("MovieEditions")
    records = p.api("api/v3/movieedition?movieId=1")
    assert [r["id"] for r in records] == [1, 2], records
    for record in records:
        expected = next(e for e in editions_before if e["Id"] == record["id"])
        assert record["movieId"] == 1 and record["state"] == "present", record
        assert record["movieFileId"] == before[record["id"]][0] and record["relativePath"] == before[record["id"]][1], record
        assert record["attributes"] == json.loads(expected["Attributes"]), record
        assert record["isPrimary"] == (record["id"] == 1) and not record["effectiveMonitored"], record
        assert record["qualityProfileId"] == 1 and record["qualityProfileName"], record
    for path, expected_code, method in [("api/v3/movieedition", 400, "GET"), ("api/v3/movieedition?movieId=0", 400, "GET"),
            ("api/v3/movieedition?movieId=-1", 400, "GET"), ("api/v3/movieedition?movieId=999999", 404, "GET"),
            ("api/v3/movieedition/999999", 404, "GET"), ("api/v3/movieedition/1", 405, "PUT"),
            ("api/v3/movieedition/1", 405, "DELETE")]:
        try:
            original_api(path, method=method)
        except urllib.error.HTTPError as error:
            assert error.code == expected_code, (error.code, error.read().decode())
        else:
            raise AssertionError("Expected rejection: " + path)
    manual = p.api("api/v3/manualimport?movieId=1")
    assert {r["movieEditionId"]: r["movieEditionName"] for r in manual} == {1: "Theatrical IMAX", 2: "Directors Cut 3D"}, manual
    path = p.source("SyntheticEditionReview", "cyan")
    for edition in records:
        item = {"path": str(path), "movieId": 1, "movieEditionId": edition["id"], "releaseGroup": "SyntheticGroup",
                "quality": {"quality": {"id": 7}}, "languages": [{"id": 1}], "indexerFlags": 0}
        processed = p.api("api/v3/manualimport", [item])[0]
        assert processed["movieEditionName"] == edition["name"] and processed["editionAssignmentValid"], processed
    ambiguous = p.api("api/v3/manualimport", [dict(item, movieEditionId=None, editionAssignmentValid=True)])[0]
    assert not ambiguous["editionAssignmentValid"] and ambiguous.get("movieEditionId") is None and ambiguous["customFormatScore"] == 0, ambiguous
    assert p.rows("MovieEditions") == editions_before and p.snapshot() == before
    profile = p.api("api/v3/qualityprofile/1")
    profile.pop("id")
    profile["name"] = "Synthetic Edition Review Profile"
    secondary = p.api("api/v3/qualityprofile", profile, "POST")
    p.stop()
    with p.database() as db:
        db.execute("""UPDATE "MovieEditions" SET "Monitored"=TRUE WHERE "Id"=1""")
        db.execute("""UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2""", (secondary["id"],))
        db.execute("""INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes","MatchRules")
            VALUES (3,1,'synthetic-collector','Synthetic Collector',TRUE,1,?,?)""",
            (json.dumps({"cut": "theatrical", "branding": "synthetic-collector", "custom-label": "Synthetic Open Label"}), json.dumps(["SyntheticExclusiveGroup$"])))
    p.start()
    records = p.api("api/v3/movieedition?movieId=1")
    assert records[0]["id"] == 1 and len(records) == 3, records
    assert next(r for r in records if r["id"] == 2)["qualityProfileName"] == secondary["name"], records
    missing = next(r for r in records if r["id"] == 3)
    assert missing["state"] == "missing" and missing.get("movieFileId") is None and not missing["effectiveMonitored"], missing
    assert missing["attributes"]["custom-label"] == "Synthetic Open Label" and missing["matchRules"] == ["SyntheticExclusiveGroup$"], missing
    p.stop()
    with p.database() as db:
        db.execute("""UPDATE "Movies" SET "Monitored"=TRUE WHERE "Id"=1""")
        db.execute("""UPDATE "MovieEditions" SET "QualityProfileId"=999999 WHERE "Id"=2""")
    p.start()
    records = p.api("api/v3/movieedition?movieId=1")
    assert next(r for r in records if r["id"] == 1)["effectiveMonitored"], records
    invalid = next(r for r in records if r["id"] == 2)
    assert invalid["state"] == "invalid" and invalid["rejectionReason"], invalid
    assert next(r for r in records if r["id"] == 1)["state"] == "present", records
    assert next(e for e in p.rows("MovieEditions") if e["Id"] == 2)["QualityProfileId"] == 999999
    p.stop()
    with p.database() as db:
        db.execute("""UPDATE "Movies" SET "Monitored"=FALSE WHERE "Id"=1""")
        db.execute("""UPDATE "MovieEditions" SET "Monitored"=FALSE,"QualityProfileId"=1 WHERE "Id" IN (1,2)""")
        db.execute("""DELETE FROM "MovieEditions" WHERE "Id"=3""")
    p.start()
    assert p.snapshot() == before
    print("PASS: read-only edition API exposes owned files, combined/custom labels, independent profiles and monitoring; invalid/missing targets stay explicit without mutation", flush=True)
    print("PASS: API rejects invalid parents/IDs and unsupported edits/deletes; manual preview/reprocess names match the validated target", flush=True)


def verify_selector_imports(secondary_profile):
    configure_browser_auth()
    before = p.snapshot()
    p.stop()
    with p.database() as db:
        db.execute("""UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2""", (secondary_profile,))
        db.execute("""INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
            VALUES (3,1,'synthetic-invalid','Synthetic Invalid Target',FALSE,999999,'{}')""")
        for file in p.rows("MovieFiles"):
            quality = json.loads(file["Quality"])
            quality["quality"] = 1
            db.execute("""UPDATE "MovieFiles" SET "Quality"=? WHERE "Id"=?""", (json.dumps(quality), file["Id"]))
    p.start()
    for edition, marker, colour in [(1, "IMAX.SyntheticSelector", "yellow"), (2, "Directors.Cut.3D.SyntheticSelector", "purple")]:
        path = p.source(marker, colour)
        previous = p.snapshot()
        subprocess.run(["node", "/src/docs/development/verify-edition-selector-browser.cjs", str(edition), str(path.parent)], check=True)
        current = p.snapshot()
        assert current[edition][0] != previous[edition][0]
        assert current[3 - edition] == previous[3 - edition]
        assert not p.rows("DownloadHistory")
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        p.stop()
        p.start()
        assert p.snapshot() == current
    assert before[1][0] != current[1][0] and before[2][0] != current[2][0]
    print("PASS: both explicit browser edition selections upgrade independently through scan/rename/restart; invalid targets cannot be selected", flush=True)


def configure_profiles():
    custom = p.api("api/v3/customformat", {"name": "Synthetic Queue Score", "includeCustomFormatWhenRenaming": False,
        "specifications": [{"name": "Synthetic Match", "implementation": "ReleaseTitleSpecification", "negate": False,
                            "required": True, "fields": [{"name": "value", "value": ".+"}]}]}, "POST")
    profile = p.api("api/v3/qualityprofile/1")
    profile.update(upgradeAllowed=True, cutoff=7)
    profile["formatItems"] = [{"format": custom["id"], "name": custom["name"], "score": 11}]
    p.api("api/v3/qualityprofile/1", profile, "PUT")
    profile.pop("id")
    profile["name"] = "Synthetic Secondary Queue Profile"
    profile["formatItems"][0]["score"] = 77
    secondary = p.api("api/v3/qualityprofile", profile, "POST")
    return secondary


def verify_profile_deletion(secondary_profile):
    before = p.snapshot()
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary_profile,))
        db.execute("""INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
            VALUES (3,1,'synthetic-profile-guard','Synthetic Empty Profile Guard',FALSE,?,'{}')""", (secondary_profile,))
    p.start()

    def rejects(profile):
        try:
            original_api("api/v3/qualityprofile/" + str(profile), method="DELETE")
        except urllib.error.HTTPError as error:
            assert error.code == 409 and "is in use" in error.read().decode(), error.code
        else:
            raise AssertionError("A referenced quality profile must not be deleted")
        assert p.api("api/v3/qualityprofile/" + str(profile))["id"] == profile
        assert p.snapshot() == before

    rejects(secondary_profile)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=1 WHERE "Id"=2')
    p.start()
    rejects(secondary_profile)
    p.stop()
    with p.database() as db:
        db.execute('DELETE FROM "MovieEditions" WHERE "Id"=3')
    p.start()
    p.api("api/v3/qualityprofile/" + str(secondary_profile), method="DELETE")
    try:
        original_api("api/v3/qualityprofile/" + str(secondary_profile))
    except urllib.error.HTTPError as error:
        assert error.code == 404, error.code
    else:
        raise AssertionError("The unreferenced profile should have been deleted")
    rejects(1)
    assert all(e["QualityProfileId"] == 1 for e in p.rows("MovieEditions"))
    print("PASS: profile HTTP deletion returns 409 for secondary/empty edition references, succeeds after release, and preserves both editions across restarts", flush=True)


def verify_naming_preview():
    before = p.snapshot()
    editions = p.rows("MovieEditions")
    files = p.rows("MovieFiles")
    history = p.rows("History")
    naming = p.api("api/v3/config/naming")
    body = {"movieId": 1, "name": "Synthetic Collector 3D IMAX", "attributes": {"framing": "imax", "stereoscopic": "3d", "custom-label": "Synthetic Open Label"},
            "matchRules": ["SyntheticExclusiveGroup$"], "monitored": True, "id": 999, "movieFileId": before[2][0]}
    preview = p.api("api/v3/movieedition/preview", body)
    assert preview["namingValid"] and preview["creationEnabled"], preview
    proposed = preview["proposedEdition"]
    assert proposed["id"] == 0 and not proposed["monitored"] and proposed["qualityProfileId"] == 1, proposed
    assert proposed["attributes"] == dict(body["attributes"], cut="theatrical"), proposed
    assert preview["referenceMovieFileId"] == before[1][0] and len(preview["samples"]) == 3, preview
    assert len({Path(s["relativePath"]).stem.casefold() for s in preview["samples"]}) == 3, preview
    for name in ["theatrical imax", "<>"]:
        rejected = p.api("api/v3/movieedition/preview", dict(body, name=name))
        assert not rejected["namingValid"] and rejected["rejections"] and not rejected["creationEnabled"], rejected
    for changes, expected in [({"movieId": 0}, 400), ({"movieId": 999999}, 404), ({"name": ""}, 400),
            ({"qualityProfileId": 999999}, 404), ({"qualityProfileId": 0}, 400), ({"matchRules": ["["]}, 400), ({"name": "Synthetic\u0000Cut"}, 400),
            ({"attributes": {"cut": "theatrical", "Cut": "directors-cut"}}, 400)]:
        try:
            original_api("api/v3/movieedition/preview", dict(body, **changes))
        except urllib.error.HTTPError as error:
            assert error.code == expected, (error.code, error.read().decode())
        else:
            raise AssertionError("Expected invalid proposal rejection")
    for overrides in [{"renameMovies": False}, {"standardMovieFormat": "{Movie Title} ({Release Year})"}]:
        p.api("api/v3/config/naming/" + str(naming["id"]), dict(naming, **overrides), "PUT")
        rejected = p.api("api/v3/movieedition/preview", body)
        assert not rejected["namingValid"] and any("{Managed Edition}" in r for r in rejected["rejections"]), rejected
    p.api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    assert p.rows("MovieEditions") == editions and p.rows("MovieFiles") == files and p.rows("History") == history
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "Name"=? WHERE "Id"=1', ("Synthetic/Variant",))
    p.start()
    rejected = p.api("api/v3/movieedition/preview", dict(body, name="Synthetic\\Variant"))
    assert not rejected["namingValid"] and any("same destination" in r for r in rejected["rejections"]), rejected
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "Name"=? WHERE "Id"=1', (editions[0]["Name"],))
    p.start()
    assert p.snapshot() == before and p.rows("MovieEditions") == editions and p.rows("MovieFiles") == files and p.rows("History") == history
    print("PASS: naming preview preserves combined/custom attributes, defaults missing cut, exposes distinct samples and rejects naming/sanitization conflicts", flush=True)
    print("PASS: invalid proposals reject; preview creates no target, enables no acquisition and changes no recorded files/history across restarts", flush=True)



def verify_statistics():
    before = p.snapshot()
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "Movies" SET "Monitored"=TRUE WHERE "Id"=1')
        db.execute('UPDATE "MovieEditions" SET "Monitored"=TRUE WHERE "MovieId"=1')
        for eid, monitored in [(3, True), (4, False)]:
            db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
                VALUES (?,1,?,?,?,1,'{}')''', (eid, "synthetic-statistics-" + str(eid), "Synthetic Missing " + str(eid), monitored))
    p.start()

    def check(files, available, monitored, missing, has_primary):
        movie = p.api("api/v3/movie/1")
        collection = p.api("api/v3/movie")
        assert len(collection) == 1 and collection[0]["id"] == 1, collection
        queried = p.api("api/v3/movie?tmdbId=" + str(movie["tmdbId"]))
        resources = [movie, collection[0], queried[0]]
        if not has_primary:
            wanted = p.api("api/v3/wanted/missing?monitored=" + str(movie["monitored"]).lower())
            assert wanted["totalRecords"] == 1, wanted
            resources += wanted["records"]
        for resource in resources:
            assert resource["hasFile"] == has_primary, resource
            assert resource["movieFileId"] == (before[1][0] if has_primary else 0), resource
            assert (resource.get("movieFile") or {}).get("id", 0) == (before[1][0] if has_primary else 0), resource
            assert resource["hasAnyEditionFile"] == (available > 0) and resource["editionCount"] == 4, resource
            assert resource["monitoredEditionCount"] == monitored and resource["missingMonitoredEditionCount"] == missing, resource
            stats = resource["statistics"]
            assert stats["movieFileCount"] == files and stats["editionFileCount"] == available, resource
            assert stats["sizeOnDisk"] == sum(f["Size"] for f in p.rows("MovieFiles")), resource
        return movie

    check(2, 2, 3, 1, True)
    assert p.snapshot() == before
    p.api("api/v3/moviefile/" + str(before[1][0]), method="DELETE")
    check(1, 1, 3, 2, False)
    other = next(f for f in p.rows("MovieFiles") if f["MovieEditionId"] == 2)
    subtitle = next(f for f in p.rows("SubtitleFiles") if f["MovieFileId"] == other["Id"])
    assert (other["Id"], other["RelativePath"], hashlib.sha256((p.MOVIE / other["RelativePath"]).read_bytes()).hexdigest(),
            subtitle["Id"], subtitle["RelativePath"], (p.MOVIE / subtitle["RelativePath"]).read_text()) == before[2]
    movie = p.api("api/v3/movie/1")
    p.api("api/v3/movie/1", dict(movie, monitored=False), "PUT")
    check(1, 1, 0, 0, False)
    assert [e["Monitored"] for e in p.rows("MovieEditions")] == [True, True, True, False]
    p.stop()
    p.start()
    check(1, 1, 0, 0, False)
    print("PASS: list/detail/TMDB statistics count both edition files once and retain primary-only hasFile after primary deletion", flush=True)
    print("PASS: secondary video/subtitle survive deletion and restart; master monitoring controls wanted counts without changing edition preferences", flush=True)



def verify_wanted(secondary_profile):
    before = p.snapshot()
    history = p.rows("History")
    profile = p.api("api/v3/qualityprofile/1")
    p.api("api/v3/qualityprofile/1", dict(profile, cutoff=1), "PUT")
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "Movies" SET "Monitored"=TRUE WHERE "Id"=1')
        db.execute('UPDATE "MovieEditions" SET "Monitored"=TRUE WHERE "MovieId"=1')
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary_profile,))
        file = next(f for f in p.rows("MovieFiles") if f["MovieEditionId"] == 2)
        quality = json.loads(file["Quality"])
        quality["quality"] = 1
        db.execute('UPDATE "MovieFiles" SET "Quality"=? WHERE "Id"=?', (json.dumps(quality, separators=(",", ":")), file["Id"]))
        for eid in [3, 4]:
            db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
                VALUES (?,1,?,?,TRUE,1,'{}')''', (eid, "synthetic-wanted-" + str(eid), "Synthetic Missing " + str(eid)))
    p.start()

    def check(endpoint, count, monitored=True):
        path = "api/v3/wanted/" + endpoint + "?page=1&pageSize=1&monitored=" + str(monitored).lower()
        page = p.api(path)
        assert page["totalRecords"] == count and len(page["records"]) == count, page
        if count:
            resource = page["records"][0]
            assert resource["id"] == 1 and resource["movieFileId"] == before[1][0] and resource["hasFile"], resource
        second = p.api(path.replace("page=1", "page=2"))
        assert second["totalRecords"] == count and not second["records"], second

    check("missing", 1)
    check("cutoff", 1)
    check("missing", 0, monitored=False)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=1 WHERE "Id"=2')
        db.execute('UPDATE "MovieEditions" SET "Monitored"=FALSE WHERE "Id" IN (3,4)')
    p.start()
    check("missing", 0)
    check("cutoff", 0)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary_profile,))
        db.execute('UPDATE "MovieEditions" SET "Monitored"=FALSE WHERE "Id"=2')
    p.start()
    check("cutoff", 0)
    movie = p.api("api/v3/movie/1")
    p.api("api/v3/movie/1", dict(movie, monitored=False), "PUT")
    check("missing", 0)
    check("cutoff", 0)
    check("missing", 1, monitored=False)
    check("cutoff", 1, monitored=False)
    p.stop()
    p.start()
    check("missing", 1, monitored=False)
    check("cutoff", 1, monitored=False)
    assert p.snapshot() == before and p.rows("History") == history
    print("PASS: legacy wanted pages include one movie for multiple missing secondary editions and use each edition's own profile cutoff", flush=True)
    print("PASS: monitoring, compact quality JSON, pagination totals and primary projection remain correct through profile changes and restart; media/history are unchanged", flush=True)



def verify_creation(secondary_profile):
    original = p.snapshot()
    p.api("api/v3/moviefile/" + str(original[2][0]), method="DELETE")
    p.stop()
    # Set up a single existing legacy target; every new target below is created by the real API.
    with p.database() as db:
        db.execute('DELETE FROM "MovieEditions" WHERE "Id"=2')
    p.start()
    history = p.rows("History")
    body = {"movieId": 1, "name": "Synthetic Directors Cut 3D", "qualityProfileId": secondary_profile,
            "attributes": {"cut": "directors-cut", "stereoscopic": "3d"}, "matchRules": ["SyntheticCreatedGroup$"],
            "id": 999, "editionKey": "legacy-primary", "monitored": True, "isPrimary": True, "movieFileId": original[1][0],
            "creationEnabled": True, "namingValid": True}

    def rejects(proposal, status):
        before = p.rows("MovieEditions")
        try:
            original_api("api/v3/movieedition", proposal)
        except urllib.error.HTTPError as error:
            assert error.code == status, (error.code, error.read().decode())
        else:
            raise AssertionError("Expected creation rejection")
        assert p.rows("MovieEditions") == before

    preview = p.api("api/v3/movieedition/preview", body)
    assert preview["namingValid"] and preview["creationEnabled"], preview
    naming = p.api("api/v3/config/naming")
    p.api("api/v3/config/naming/" + str(naming["id"]), dict(naming, renameMovies=False), "PUT")
    rejects(body, 409)
    p.api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    destination = p.MOVIE / preview["samples"][-1]["relativePath"]
    for suffix in [".avi", ".en.srt"]:
        conflict = destination.with_suffix(suffix)
        conflict.write_bytes(b"Synthetic conflicting file bytes")
        rejects(body, 409)
        assert conflict.read_bytes() == b"Synthetic conflicting file bytes"
        conflict.unlink()
    for changes, status in [({"name": "Theatrical IMAX"}, 409), ({"name": "<>"}, 409), ({"name": ""}, 400),
            ({"movieId": 999999}, 404), ({"qualityProfileId": 999999}, 404), ({"matchRules": ["["]}, 400)]:
        rejects(dict(body, **changes), status)
    request = p.urllib.request.Request("http://127.0.0.1:17878/api/v3/movieedition", data=json.dumps(body).encode(),
            headers={"X-Api-Key": p.KEY, "Content-Type": "application/json"}, method="POST")
    with p.urllib.request.urlopen(request, timeout=15) as response:
        created = json.loads(response.read())
        assert response.status == 201 and response.headers["Location"].endswith("/movieedition/" + str(created["id"])), response.status
    target = created["id"]
    assert target not in [1, 2, 999] and created["editionKey"].startswith("custom-"), created
    assert not created["monitored"] and not created["effectiveMonitored"] and not created["isPrimary"], created
    assert created["state"] == "missing" and created.get("movieFileId") is None and created["qualityProfileId"] == secondary_profile, created
    assert p.api("api/v3/movieedition/" + str(target)) == created
    assert p.rows("History") == history and len(p.rows("MovieFiles")) == 1
    rejects(dict(body, name=body["name"].upper()), 409)
    for method in ["PUT", "DELETE"]:
        try:
            original_api("api/v3/movieedition/" + str(target), body if method == "PUT" else None, method)
        except urllib.error.HTTPError as error:
            assert error.code == 405, error.code
        else:
            raise AssertionError("Edition editing/deletion must remain unavailable")

    def capture():
        result = {}
        for file in p.rows("MovieFiles"):
            subtitle = next(e for e in p.rows("SubtitleFiles") if e["MovieFileId"] == file["Id"])
            result[file["MovieEditionId"]] = (file["Id"], file["RelativePath"], hashlib.sha256((p.MOVIE / file["RelativePath"]).read_bytes()).hexdigest(),
                subtitle["Id"], subtitle["RelativePath"], (p.MOVIE / subtitle["RelativePath"]).read_text())
        assert {e["Id"]: e["MovieFileId"] for e in p.rows("MovieEditions") if e["MovieFileId"] is not None} == {eid: f[0] for eid, f in result.items()}
        assert p.api("api/v3/movie/1")["movieFileId"] == result[1][0]
        return result

    p.manual(target, "Directors.Cut.3D.SyntheticCreatedGroup", "pink")
    assert capture()[1] == original[1] and len(capture()) == 2
    custom_body = {"movieId": 1, "name": "Synthetic Collector 3D IMAX", "attributes": {"stereoscopic": "3d", "framing": "imax", "custom-label": "Synthetic Collector"}}
    custom = p.api("api/v3/movieedition", custom_body)
    assert custom["attributes"] == dict(custom_body["attributes"], cut="theatrical") and not custom["monitored"], custom
    p.manual(custom["id"], "Theatrical.3D.IMAX.SyntheticCollectorGroup", "purple")
    before = capture()
    assert len(before) == 3 and before[1] == original[1]
    for eid, colour in [(target, "white"), (1, "blue")]:
        p.manual(eid, "SyntheticCreatedUpgrade" + str(eid), colour)
        after = capture()
        assert after[eid][0] != before[eid][0] and after[eid][2] != before[eid][2]
        assert all(after[other] == before[other] for other in before if other != eid)
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        p.stop()
        p.start()
        assert capture() == after
        before = after
    for record in [created, custom]:
        current = p.api("api/v3/movieedition/" + str(record["id"]))
        assert current["editionKey"] == record["editionKey"] and current["attributes"] == record["attributes"] and not current["monitored"], current
        assert current["state"] == "present", current
    assert not p.rows("DownloadHistory")
    print("PASS: real POST creates unmonitored targets with stable keys; missing cut defaults while combined/custom attributes persist", flush=True)
    print("PASS: fresh naming/disk validation rejects collisions and spoofed ownership/monitoring cannot alter the primary; editing/deletion remain gated", flush=True)
    print("PASS: API-created Director's Cut / 3D and Theatrical / 3D / IMAX import, scan, rename and restart; upgrades preserve every other edition's video/subtitle bytes and IDs", flush=True)



def verify_creation_browser(secondary_profile):
    before = p.snapshot()
    configure_browser_auth()
    sources = [p.source("Theatrical.3D.IMAX.SyntheticBrowserGroup", "purple"), p.source("Directors.Cut.3D.SyntheticBrowserGroup", "pink")]
    result = subprocess.run(["node", "/src/docs/development/verify-edition-management-browser.cjs", str(secondary_profile), *map(str, sources)],
                            text=True, capture_output=True)
    print(result.stdout, end="", flush=True)
    print(result.stderr, end="", file=sys.stderr, flush=True)
    result.check_returncode()
    created = json.loads(next(line.removeprefix("CREATED_EDITIONS=") for line in result.stdout.splitlines() if line.startswith("CREATED_EDITIONS=")))

    def capture():
        return {file["MovieEditionId"]: (file["Id"], file["RelativePath"], hashlib.sha256((p.MOVIE / file["RelativePath"]).read_bytes()).hexdigest(),
                    extra["Id"], extra["RelativePath"], (p.MOVIE / extra["RelativePath"]).read_text())
                for file in p.rows("MovieFiles") for extra in p.rows("SubtitleFiles") if extra["MovieFileId"] == file["Id"]}

    current = capture()
    assert len(current) == 4 and all(current[eid] == value for eid, value in before.items())
    for edition in created:
        previous = capture()
        p.manual(edition["id"], "SyntheticBrowserUpgrade" + str(edition["id"]), "yellow")
        current = capture()
        assert current[edition["id"]][0] != previous[edition["id"]][0]
        assert all(current[eid] == previous[eid] for eid in current if eid != edition["id"])
        p.command("RescanMovie", movieId=1)
        p.command("RenameMovie", movieIds=[1])
        p.stop()
        p.start()
        assert capture() == current
        saved = p.api("api/v3/movieedition/" + str(edition["id"]))
        assert saved["editionKey"] == edition["editionKey"] and saved["attributes"] == edition["attributes"] and not saved["monitored"]
    assert not p.rows("DownloadHistory")
    print("PASS: both browser-created combined editions upgrade independently and survive scan/rename/restart, preserving all other videos/subtitles and stable keys/labels", flush=True)



def verify_bulk_file_parents():
    before = p.snapshot()
    primary = next(f for f in p.rows("MovieFiles") if f["MovieEditionId"] == 1)
    secondary = next(f for f in p.rows("MovieFiles") if f["MovieEditionId"] == 2)
    subtitle = next(e for e in p.rows("SubtitleFiles") if e["MovieFileId"] == primary["Id"])
    other = p.LIBRARY / "Synthetic Solar Voyage (2026)"
    other.mkdir()
    (other / primary["RelativePath"]).write_bytes((p.MOVIE / primary["RelativePath"]).read_bytes())
    (other / subtitle["RelativePath"]).write_bytes((p.MOVIE / subtitle["RelativePath"]).read_bytes())

    def insert(db, table, row):
        assert all(key.isalpha() for key in row)
        db.execute('INSERT INTO "' + table + '" (' + ','.join('"' + key + '"' for key in row) + ') VALUES (' + ','.join('?' for _ in row) + ')', tuple(row.values()))

    p.stop()
    with p.database() as db:
        insert(db, "MovieMetadata", dict(p.rows("MovieMetadata")[0], Id=2, TmdbId=-1000002, Title="Synthetic Solar Voyage", CleanTitle="syntheticsolarvoyage", OriginalTitle="Synthetic Solar Voyage", CleanOriginalTitle="syntheticsolarvoyage"))
        insert(db, "Movies", dict(p.rows("Movies")[0], Id=2, Path=str(other), PrimaryEditionId=3, MovieFileId=900, MovieMetadataId=2))
        db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","MovieFileId","Attributes")
            VALUES (3,2,'synthetic-solar','Synthetic Solar Theatrical',FALSE,1,900,'{"cut":"theatrical"}')''')
        insert(db, "MovieFiles", dict(primary, Id=900, MovieId=2, MovieEditionId=3))
        insert(db, "SubtitleFiles", dict(subtitle, Id=900, MovieId=2, MovieFileId=900))
    p.start()
    ids = [secondary["Id"], 900]

    def check_paths(resources):
        assert len(resources) == 2
        for resource in resources:
            expected = p.MOVIE if resource["movieId"] == 1 else other
            assert resource["path"] == str(expected / resource["relativePath"]), resource

    check_paths(p.api("api/v3/moviefile/bulk", [{"id": fid, "releaseGroup": "SyntheticBulkGroup"} for fid in ids], "PUT"))
    check_paths(p.api("api/v3/moviefile/editor", {"movieFileIds": ids, "releaseGroup": "SyntheticLegacyBulkGroup"}, "PUT"))
    check_paths(p.api("api/v3/moviefile?movieFileIds=" + str(ids[0]) + "&movieFileIds=900"))
    for bad in [[], [ids[0], ids[0]], [0]]:
        try:
            original_api("api/v3/moviefile/bulk", {"movieFileIds": bad}, "DELETE")
        except urllib.error.HTTPError as error:
            assert error.code == 400, (error.code, error.read().decode())
        else:
            raise AssertionError("Invalid bulk selections must reject")
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieFiles" SET "RelativePath"=? WHERE "Id"=900', (str(Path("..") / p.MOVIE.name / primary["RelativePath"]),))
    p.start()
    try:
        original_api("api/v3/moviefile/bulk", {"movieFileIds": ids}, "DELETE")
    except urllib.error.HTTPError as error:
        assert error.code == 409, (error.code, error.read().decode())
    else:
        raise AssertionError("Escaping file paths must reject the whole selection before deletion")
    assert p.snapshot() == before and (other / primary["RelativePath"]).exists()
    assert len(p.rows("MovieFiles")) == 3 and len(p.rows("SubtitleFiles")) == 3
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieFiles" SET "RelativePath"=? WHERE "Id"=900', (primary["RelativePath"],))
    p.start()
    p.api("api/v3/moviefile/bulk", {"movieFileIds": ids}, "DELETE")
    assert {f["Id"] for f in p.rows("MovieFiles")} == {primary["Id"]}
    assert {e["Id"] for e in p.rows("SubtitleFiles")} == {subtitle["Id"]}
    assert (p.MOVIE / primary["RelativePath"]).exists() and not (p.MOVIE / secondary["RelativePath"]).exists()
    assert not (other / primary["RelativePath"]).exists() and not (other / subtitle["RelativePath"]).exists()
    p.stop()
    p.start()
    p.command("RescanMovie", movieId=1)
    assert hashlib.sha256((p.MOVIE / primary["RelativePath"]).read_bytes()).hexdigest() == before[1][2]
    assert (p.MOVIE / subtitle["RelativePath"]).read_text() == before[1][5]
    assert p.api("api/v3/movie/1")["movieFileId"] == primary["Id"] and p.api("api/v3/movie/1")["hasFile"]
    assert not p.api("api/v3/movie/2")["hasFile"]
    print("PASS: cross-movie bulk/legacy edits and list responses use each file's own parent path", flush=True)
    print("PASS: invalid/escaping bulk selections reject before deletion; valid deletion removes only both selected parents' files/subtitles, preserving the unselected primary through restart", flush=True)



def verify_file_profiles(secondary_profile):
    before = p.snapshot()
    history = p.rows("History")
    default = p.api("api/v3/qualityprofile/1")
    p.api("api/v3/qualityprofile/1", dict(default, cutoff=1), "PUT")
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary_profile,))
        for file in p.rows("MovieFiles"):
            quality = json.loads(file["Quality"])
            quality["quality"] = 1
            db.execute('UPDATE "MovieFiles" SET "Quality"=? WHERE "Id"=?', (json.dumps(quality), file["Id"]))
    p.start()

    def validate(resources):
        assert len(resources) == 2, resources
        for resource in resources:
            secondary = resource["movieEditionId"] == 2
            assert resource["editionContextValid"] and resource["customFormatScore"] == (77 if secondary else 11), resource
            assert resource["qualityProfileId"] == (secondary_profile if secondary else 1), resource
            assert resource["qualityCutoffNotMet"] == secondary, resource
            assert resource["movieEditionName"] == ("Directors Cut 3D" if secondary else "Theatrical IMAX"), resource

    files = p.api("api/v3/moviefile?movieId=1")
    validate(files)
    validate([p.api("api/v3/moviefile/" + str(file["id"])) for file in files])
    validate(p.api("api/v3/moviefile/bulk", [{"id": file["id"], "releaseGroup": "SyntheticFileProfileGroup"} for file in files], "PUT"))
    validate(p.api("api/v3/moviefile/editor", {"movieFileIds": [file["id"] for file in files], "releaseGroup": "SyntheticLegacyProfileGroup"}, "PUT"))
    configure_browser_auth()
    subprocess.run(["node", "/src/docs/development/verify-edition-file-profiles-browser.cjs", "valid"], check=True)
    for owner, profile in [(None, secondary_profile), (999999, secondary_profile), (2, 999999)]:
        p.stop()
        with p.database() as db:
            db.execute('UPDATE "MovieFiles" SET "MovieEditionId"=? WHERE "Id"=?', (owner, before[2][0]))
            db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (profile,))
        p.start()
        records = p.api("api/v3/moviefile?movieId=1")
        invalid = next(file for file in records if file["id"] == before[2][0])
        assert invalid["editionContextValid"] is False and invalid["editionContextError"], invalid
        assert invalid.get("customFormatScore") is None and invalid.get("qualityProfileId") is None and not invalid["customFormats"], invalid
        assert next(file for file in records if file["id"] == before[1][0])["customFormatScore"] == 11
        primary_resource = p.api("api/v3/movie/1")
        assert primary_resource["qualityProfileId"] == 1 and primary_resource["movieFileId"] == before[1][0], primary_resource
        if profile == 999999:
            subprocess.run(["node", "/src/docs/development/verify-edition-file-profiles-browser.cjs", "invalid"], check=True)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieFiles" SET "MovieEditionId"=2 WHERE "Id"=?', (before[2][0],))
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary_profile,))
    p.start()
    validate(p.api("api/v3/moviefile?movieId=1"))
    assert p.snapshot() == before and p.rows("History") == history
    print("PASS: file list/detail/bulk/legacy resources score 11/77 and evaluate cutoff using the actual edition profile without changing primary policy", flush=True)
    print("PASS: unassigned/foreign-target/missing-profile files expose an explicit invalid context, omit score/profile and never borrow the default; file/subtitle bytes and history survive restart", flush=True)



def verify_primary_context(edition_profile):
    before = p.snapshot()
    default = p.api("api/v3/qualityprofile/1")
    p.api("api/v3/qualityprofile/1", dict(default, cutoff=1), "PUT")
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=1', (edition_profile,))
        db.execute('UPDATE "Movies" SET "Monitored"=TRUE WHERE "Id"=1')
        db.execute('UPDATE "MovieEditions" SET "Monitored"=TRUE WHERE "MovieId"=1')
        db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
            VALUES (3,1,'synthetic-missing','Synthetic Missing Target',TRUE,1,'{}')''')
        db.execute('UPDATE "MovieMetadata" SET "PhysicalRelease"=? WHERE "Id"=1', ("2026-11-10T00:00:00Z",))
        for file in p.rows("MovieFiles"):
            quality = json.loads(file["Quality"])
            quality["quality"] = 1
            db.execute('UPDATE "MovieFiles" SET "Quality"=? WHERE "Id"=?', (json.dumps(quality), file["Id"]))
    p.start()
    calendar = "api/v3/calendar?start=2026-11-01&end=2026-12-01&unmonitored=true"

    def resources(include_cutoff=True):
        result = [p.api("api/v3/movie/1"), *p.api("api/v3/movie"), *p.api("api/v3/movie?tmdbId=-1000001"), *p.api(calendar)]
        missing = p.api("api/v3/wanted/missing")
        assert missing["totalRecords"] == 1, missing
        result += missing["records"]
        if include_cutoff:
            cutoff = p.api("api/v3/wanted/cutoff")
            assert cutoff["totalRecords"] == 1, cutoff
            result += cutoff["records"]
        assert len(result) == (6 if include_cutoff else 5), result
        return result

    editions = p.rows("MovieEditions")
    files = p.rows("MovieFiles")
    history = p.rows("History")
    for resource in resources():
        assert resource["qualityProfileId"] == 1 and resource["hasFile"] and resource["movieFileId"] == before[1][0], resource
        file = resource["movieFile"]
        assert file["id"] == before[1][0] and file["movieEditionId"] == 1 and file["editionContextValid"], resource
        assert file["qualityProfileId"] == edition_profile and file["qualityCutoffNotMet"], resource
        if "customFormatScore" in file:
            assert file["customFormatScore"] == 77, resource
    assert p.rows("MovieEditions") == editions and p.rows("MovieFiles") == files and p.rows("History") == history
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=999999 WHERE "Id"=1')
    p.start()
    for resource in resources(include_cutoff=False):
        file = resource["movieFile"]
        assert file["editionContextValid"] is False and file["editionContextError"], resource
        assert file.get("qualityProfileId") is None and file.get("customFormatScore") is None, resource
        assert resource["qualityProfileId"] == 1 and resource["movieFileId"] == before[1][0], resource
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=1', (edition_profile,))
        db.execute('UPDATE "Movies" SET "PrimaryEditionId"=2,"MovieFileId"=? WHERE "Id"=1', (before[2][0],))
    p.start()
    for resource in resources():
        assert resource["qualityProfileId"] == 1 and resource["movieFileId"] == before[2][0], resource
        file = resource["movieFile"]
        assert file["id"] == before[2][0] and file["qualityProfileId"] == 1 and not file["qualityCutoffNotMet"], resource
        if "customFormatScore" in file:
            assert file["customFormatScore"] == 11, resource
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "Movies" SET "PrimaryEditionId"=1,"MovieFileId"=? WHERE "Id"=1', (before[1][0],))
    p.start()
    assert p.snapshot() == before and p.rows("History") == history
    print("PASS: list/detail/TMDB/calendar/wanted embedded files use the primary edition profile while retaining the movie default", flush=True)
    print("PASS: invalid primary context cannot borrow the default; primary selection and both files/subtitles survive real process restarts", flush=True)


def run():
    scenario = sys.argv[1] if len(sys.argv) > 1 else "ignore"
    if scenario not in {"ignore", "completed", "failure", "bulk", "manual", "manual-ui", "edition-read", "manual-select", "profiles", "naming-preview", "statistics", "wanted", "creation", "creation-ui", "bulk-files", "file-profiles", "primary-context"}:
        raise ValueError("Unknown synthetic-client scenario: " + scenario)
    if sys.argv[2:] == ["--postgres"]:
        p.DATABASE.enable_postgres()
    elif sys.argv[2:]:
        raise ValueError("Only --postgres is supported after the scenario")
    p.run()
    if scenario == "edition-read":
        verify_edition_reads()
        return
    if scenario == "bulk-files":
        verify_bulk_file_parents()
        return
    if scenario == "statistics":
        verify_statistics()
        return
    if scenario == "naming-preview":
        verify_naming_preview()
        return
    secondary = configure_profiles()
    if scenario == "primary-context":
        verify_primary_context(secondary["id"])
        return
    if scenario == "file-profiles":
        verify_file_profiles(secondary["id"])
        return
    if scenario == "creation-ui":
        verify_creation_browser(secondary["id"])
        return
    if scenario == "creation":
        verify_creation(secondary["id"])
        return
    if scenario == "wanted":
        verify_wanted(secondary["id"])
        return
    if scenario == "profiles":
        verify_profile_deletion(secondary["id"])
        return
    if scenario == "manual-select":
        verify_selector_imports(secondary["id"])
        return
    before = p.snapshot()
    p.stop()
    clients = [SyntheticSab(1), SyntheticSab(2)]
    SERVERS.extend(clients)
    seed(clients, secondary["id"])
    if scenario in ("completed", "manual-ui"):
        with p.database() as db:
            for file in p.rows("MovieFiles"):
                quality = json.loads(file["Quality"])
                quality["quality"] = 1
                db.execute("""UPDATE "MovieFiles" SET "Quality"=? WHERE "Id"=?""", (json.dumps(quality), file["Id"]))
    p.start()
    first = queue([1, 2])
    assert first[1]["id"] != first[2]["id"], first
    assert p.snapshot() == before
    print("PASS: real HTTP polling recovers two editions with the same download ID on distinct clients", flush=True)
    p.stop()
    with p.database() as db:
        db.execute("""UPDATE "MovieEditions" SET "MatchRules"='["UnmatchedSyntheticGroup$"]',"RuleRevision"="RuleRevision"+1""")
    p.start()
    restored = queue([1, 2])
    assert {k: r["id"] for k, r in restored.items()} == {k: r["id"] for k, r in first.items()}
    assert p.snapshot() == before
    print("PASS: both assignments and client-specific queue IDs survive an application process restart and rule edits", flush=True)
    if scenario == "manual-ui":
        verify_browser_manual(clients, before)
        return
    if scenario == "manual":
        verify_manual(clients)
        return
    if scenario == "completed":
        verify_completions(clients, before)
        return
    if scenario == "bulk":
        p.api("api/v3/queue/bulk?removeFromClient=true&blocklist=false", {"ids": [r["id"] for r in restored.values()]}, "DELETE")
        queue([])
        assert all(c.deleted and not c.rejected for c in clients)
        assert p.snapshot() == before
        print("PASS: one HTTP bulk request removes both same-ID client entries independently and preserves media", flush=True)
        return
    if scenario == "failure":
        clients[0].mode = "failed"
        p.command("RefreshMonitoredDownloads")
        p.command("ProcessMonitoredDownloads")
        queue([2])
        failed = [r for r in p.rows("DownloadHistory") if r["EventType"] == 3]
        assert len(failed) == 1 and failed[0]["DownloadClientId"] == 41 and failed[0]["MovieEditionId"] == 1, failed
        assert json.loads(failed[0]["Data"])["movieEditionName"] == "Theatrical IMAX"
        assert p.snapshot() == before
        p.stop()
        p.start()
        queue([2])
        assert p.snapshot() == before and all(not c.deleted and not c.rejected for c in clients)
        assert not any(c["name"] == "MoviesSearch" for c in p.api("api/v3/command"))
        print("PASS: failed HTTP client settles only its edition across restart, preserves media and schedules no broad retry", flush=True)
        return
    p.api("api/v3/queue/" + str(restored[1]["id"]) + "?removeFromClient=false&blocklist=false", method="DELETE")
    queue([2])
    assert not clients[0].deleted and not clients[1].deleted
    ignored = [r for r in p.rows("DownloadHistory") if r["EventType"] == 4]
    assert len(ignored) == 1 and ignored[0]["DownloadClientId"] == 41 and ignored[0]["MovieEditionId"] == 1, ignored
    assert p.snapshot() == before
    p.stop()
    p.start()
    remaining = queue([2])
    assert remaining[2]["id"] == first[2]["id"]
    print("PASS: HTTP ignore settles only its client/edition and remains isolated after restart", flush=True)
    p.api("api/v3/queue/bulk?removeFromClient=true&blocklist=false", {"ids": [remaining[2]["id"]]}, "DELETE")
    queue([])
    assert not clients[0].deleted and clients[1].deleted
    assert p.snapshot() == before
    print("PASS: HTTP bulk removal reaches only the selected synthetic client and leaves media untouched", flush=True)
    assert all(not client.rejected for client in clients), [c.rejected for c in clients]
    print("PASS: no acquisition request or unexpected client operation was made", flush=True)


if __name__ == "__main__":
    try:
        run()
    finally:
        p.stop()
        try:
            for client in SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
