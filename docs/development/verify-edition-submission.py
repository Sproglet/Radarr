"""Legacy submission and managed-edition rejection using disposable HTTP fixtures.

Run in the externally offline SDK runner, optionally with --postgres. The only
accepted submission is a generated fake NZB to a loopback synthetic SAB service.
No real download client, indexer, production configuration or media is accessed.
"""

import importlib.util
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sys
import threading
import urllib.parse

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p
NZB = b'''<?xml version="1.0"?><nzb xmlns="http://www.newzbin.com/DTD/2003/nzb"><file poster="synthetic@example.invalid" date="1794355200" subject="SyntheticLegacyOnly.bin"><groups><group>alt.binaries.synthetic.invalid</group></groups><segments><segment bytes="36" number="1">SyntheticLegacyOnly@example.invalid</segment></segments></file></nzb>'''


class LegacySubmissionSab(c.SyntheticSab):
    def __init__(self):
        super().__init__(1)
        self.deleted = True
        self.submitted = 0
        owner = self
        original = self.server.RequestHandlerClass

        class Handler(original):
            def do_POST(self):
                query = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
                body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
                if query.get("mode") != ["addfile"] or query.get("cat") != ["movies"] or NZB not in body:
                    owner.rejected.append("Unexpected legacy submission")
                    self.send_error(409)
                    return
                owner.submitted += 1
                owner.deleted = False
                result = json.dumps({"status": True, "nzo_ids": [c.SHARED]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(result)))
                self.end_headers()
                self.wfile.write(result)

        self.server.RequestHandlerClass = Handler


def seed(secondary, client):
    with p.database() as db:
        for mid, title, clean, eid in [(2, "Synthetic Legacy Comet", "syntheticlegacycomet", 3),
                                       (3, "Synthetic Managed Meteor", "syntheticmanagedmeteor", 4)]:
            folder = p.LIBRARY / (title + " (2026)")
            folder.mkdir()
            db.execute('''INSERT INTO "MovieMetadata" ("Id","TmdbId","Images","Title","CleanTitle","OriginalTitle","CleanOriginalTitle","OriginalLanguage","Status","Runtime","Recommendations","Year","Genres","Keywords","Ratings","PhysicalRelease")
                VALUES (?,?,'[]',?,?,?,?,1,3,100,'[]',2026,'[]','[]','{}','2026-01-01T00:00:00Z')''',
                (mid, -1000000 - mid, title, clean, title, clean))
            db.execute('''INSERT INTO "Movies" ("Id","Path","Monitored","QualityProfileId","MovieFileId","MinimumAvailability","MovieMetadataId","Tags","PrimaryEditionId")
                VALUES (?,?,TRUE,1,0,3,?,'[]',?)''', (mid, str(folder), mid, eid))
            attrs = {} if mid == 2 else {"cut": "directors-cut", "stereoscopic": "3d"}
            db.execute('''INSERT INTO "MovieEditions" ("Id","MovieId","EditionKey","Name","Monitored","QualityProfileId","Attributes")
                VALUES (?,?,?, ?,TRUE,?,?)''', (eid, mid, "synthetic-submission-" + str(mid),
                "Theatrical" if mid == 2 else "Directors Cut 3D", 1 if mid == 2 else secondary, json.dumps(attrs)))
        settings = {"host": "127.0.0.1", "port": client.port, "apiKey": "synthetic-api-key", "movieCategory": "movies"}
        db.execute('''INSERT INTO "DownloadClients" ("Id","Enable","Name","Implementation","Settings","ConfigContract","Priority","RemoveCompletedDownloads","RemoveFailedDownloads","Tags")
            VALUES (41,TRUE,'Synthetic Submission SAB','Sabnzbd',?,'SabnzbdSettings',1,FALSE,FALSE,'[]')''', (json.dumps(settings),))


def run(postgres):
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    media = p.snapshot()
    p.stop()
    client = LegacySubmissionSab()
    c.SERVERS.append(client)
    requests = []

    class Source(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            assert self.path == "/SyntheticLegacyOnly.nzb", self.path
            requests.append(self.path)
            self.send_response(200)
            self.send_header("Content-Type", "application/x-nzb")
            self.send_header("Content-Length", str(len(NZB)))
            self.end_headers()
            self.wfile.write(NZB)

    source = ThreadingHTTPServer(("127.0.0.1", 0), Source)
    c.SERVERS.append(type("OwnedServer", (), {"server": source})())
    threading.Thread(target=source.serve_forever, daemon=True).start()
    seed(secondary, client)
    p.start()
    url = "http://127.0.0.1:" + str(source.server_address[1]) + "/SyntheticLegacyOnly.nzb"

    def push(title):
        return p.api("api/v3/release/push", {"title": title, "downloadUrl": url, "protocol": "usenet",
            "publishDate": "2026-11-11T00:00:00Z", "size": 10737418240, "indexer": "Synthetic Loopback Source", "downloadClientId": 41})[0]

    legacy = push("Synthetic.Legacy.Comet.2026.1080p.BluRay-SyntheticGroup")
    assert legacy["approved"] and legacy["mappedMovieId"] == 2, legacy
    assert client.submitted == 1 and requests == ["/SyntheticLegacyOnly.nzb"] and not client.rejected
    for table in ["History", "DownloadHistory"]:
        grabbed = [r for r in p.rows(table) if r["EventType"] == 1 and r["MovieId"] == 2]
        assert len(grabbed) == 1 and grabbed[0]["MovieEditionId"] is None and grabbed[0]["DownloadId"] == c.SHARED, grabbed
    print("PASS: unchanged legacy single-target submission traverses the real API/decision/download pipeline, sends only the generated NZB to the synthetic client and preserves unassigned legacy grab semantics", flush=True)
    for title in ["Synthetic.Lunar.Voyage.2026.IMAX.1080p.BluRay-SyntheticGroup",
                  "Synthetic.Managed.Meteor.2026.Directors.Cut.3D.1080p.BluRay-SyntheticGroup"]:
        rejected = push(title)
        assert not rejected["approved"] and not rejected["downloadAllowed"], rejected
        assert any("Multi-edition acquisition is disabled" in reason for reason in rejected["rejections"]), rejected
        assert client.submitted == 1 and len(requests) == 1 and not client.rejected
    before = {table: p.rows(table) for table in ["Movies", "MovieEditions", "MovieFiles", "SubtitleFiles", "History", "DownloadHistory"]}
    p.stop()
    p.start()
    p.command("RefreshMonitoredDownloads")
    assert before == {table: p.rows(table) for table in before} and p.snapshot() == media
    assert client.submitted == 1 and len(requests) == 1 and not client.rejected
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: multi-target and independently managed single-target pushes remain rejected before NZB/client IO; restart preserves all ownership, legacy intent and both media/subtitle pairs without another submission or search", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise ValueError("Only --postgres is supported")
        run(sys.argv[1:] == ["--postgres"])
    finally:
        p.stop()
        try:
            for client in c.SERVERS:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()
