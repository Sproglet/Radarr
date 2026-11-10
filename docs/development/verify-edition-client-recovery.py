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
            ("api/v3/movieedition?movieId=1", 405, "POST")]:
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
    print("PASS: API rejects invalid parents and write methods; manual preview/reprocess names match the validated target", flush=True)


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
    assert preview["namingValid"] and not preview["creationEnabled"], preview
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


def run():
    scenario = sys.argv[1] if len(sys.argv) > 1 else "ignore"
    if scenario not in {"ignore", "completed", "failure", "bulk", "manual", "manual-ui", "edition-read", "manual-select", "profiles", "naming-preview"}:
        raise ValueError("Unknown synthetic-client scenario: " + scenario)
    if sys.argv[2:] == ["--postgres"]:
        p.DATABASE.enable_postgres()
    elif sys.argv[2:]:
        raise ValueError("Only --postgres is supported after the scenario")
    p.run()
    if scenario == "edition-read":
        verify_edition_reads()
        return
    if scenario == "naming-preview":
        verify_naming_preview()
        return
    secondary = configure_profiles()
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
