"""Offline SQLite process-restart and HTTP queue regression with synthetic SAB APIs.

Run only inside a disposable SDK container with this checkout mounted at /src,
a complete net10.0 application build, Python/ffmpeg, and no external network.
No real client is contacted and acquisition requests to the fake APIs fail.
"""

import importlib.util
import json
from pathlib import Path
import shutil
import sqlite3
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
                        "status": "Completed" if owner.mode == "completed" else "Failed",
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
    with sqlite3.connect(p.CONFIG / "radarr.db") as db:
        db.execute("UPDATE MovieEditions SET QualityProfileId=? WHERE Id=2", (secondary_profile,))
        for client, edition, label in zip(clients, [1, 2], ["Theatrical.IMAX", "Directors.Cut.3D"]):
            settings = {"host": "127.0.0.1", "port": client.port, "apiKey": "synthetic-api-key", "movieCategory": "movies"}
            db.execute('''INSERT INTO DownloadClients
                (Id,Enable,Name,Implementation,Settings,ConfigContract,Priority,RemoveCompletedDownloads,RemoveFailedDownloads,Tags)
                VALUES (?,1,?,'Sabnzbd',?,'SabnzbdSettings',1,0,0,'[]')''',
                (client.client_id, "Synthetic SAB " + str(client.number), json.dumps(settings)))
            title = "Synthetic.Lunar.Voyage.2026." + label + ".1080p.BluRay-SyntheticGroup"
            edition_row = db.execute("SELECT Name,RuleRevision FROM MovieEditions WHERE Id=?", (edition,)).fetchone()
            data = {"downloadClientId": str(client.client_id), "movieEditionName": edition_row[0], "indexer": "Synthetic Indexer",
                    "publishedDate": "2026-11-09T00:00:00Z", "size": "104857600", "protocol": "1", "releaseSource": "Rss"}
            release = {"title": title, "guid": "synthetic-guid-" + str(edition), "indexer": "Synthetic Indexer", "indexerId": 77,
                       "size": 104857600, "downloadProtocol": "usenet", "publishDate": "2026-11-09T00:00:00Z"}
            quality = json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}})
            db.execute('''INSERT INTO History (MovieId,MovieEditionId,EditionRuleRevision,SourceTitle,Quality,Date,EventType,Data,Languages,DownloadId)
                VALUES (1,?,?,?,?,?,1,?,'[1]',?)''',
                (edition, edition_row[1], title, quality, "2026-11-09T00:00:00Z", json.dumps(data), SHARED))
            db.execute('''INSERT INTO DownloadHistory (MovieId,MovieEditionId,EditionRuleRevision,SourceTitle,Date,EventType,DownloadId,Protocol,IndexerId,DownloadClientId,Release,Data)
                VALUES (1,?,?,?,'2026-11-09T00:00:00Z',1,?,1,77,?,?,?)''',
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


def run():
    p.run()
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
    before = p.snapshot()
    p.stop()
    clients = [SyntheticSab(1), SyntheticSab(2)]
    SERVERS.extend(clients)
    seed(clients, secondary["id"])
    scenario = sys.argv[1] if len(sys.argv) > 1 else "ignore"
    if scenario == "completed":
        with sqlite3.connect(p.CONFIG / "radarr.db") as db:
            for file in p.rows("MovieFiles"):
                quality = json.loads(file["Quality"])
                quality["quality"] = 1
                db.execute("UPDATE MovieFiles SET Quality=? WHERE Id=?", (json.dumps(quality), file["Id"]))
    p.start()
    first = queue([1, 2])
    assert first[1]["id"] != first[2]["id"], first
    assert p.snapshot() == before
    print("PASS: real HTTP polling recovers two editions with the same download ID on distinct clients", flush=True)
    p.stop()
    with sqlite3.connect(p.CONFIG / "radarr.db") as db:
        db.execute("UPDATE MovieEditions SET MatchRules='[\"UnmatchedSyntheticGroup$\"]',RuleRevision=RuleRevision+1")
    p.start()
    restored = queue([1, 2])
    assert {k: r["id"] for k, r in restored.items()} == {k: r["id"] for k, r in first.items()}
    assert p.snapshot() == before
    print("PASS: both assignments and client-specific queue IDs survive an application process restart and rule edits", flush=True)
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
        for client in SERVERS:
            client.server.shutdown()
            client.server.server_close()
        shutil.rmtree(p.ROOT)
