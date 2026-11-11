"""Real, paused SABnzbd queue/restart checks with disposable edition data.

Requires a dedicated, externally isolated SAB container at radarr-edition-sab:8080
with the synthetic key below, no Usenet servers, and its owned config directory
mounted below /src/_temp/edition-test-results. See the status checkpoint for the
exact image/configuration. This seeds recorded intent for directly uploaded fake
NZBs; it never enables Radarr acquisition or downloads any Usenet content.
"""

import argparse
import importlib.util
import json
from pathlib import Path
import sys
import time
import urllib.parse
import urllib.request
import uuid

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p
SAB_KEY = "0123456789abcdef0123456789abcdef"
CLIENT_ID = 41


class DisposableSab:
    def __init__(self, root):
        self.root = root.resolve()
        assert self.root.is_relative_to(p.RESULTS.resolve()) and self.root.name.startswith("real-sab-")
        assert (self.root / ".edition-test-owned").read_text().startswith("synthetic disposable SAB client ")
        self.created = []

    def api(self, mode, body=None, content_type=None, **fields):
        url = "http://radarr-edition-sab:8080/api?" + urllib.parse.urlencode({"apikey": SAB_KEY, "output": "json", "mode": mode, **fields})
        request = urllib.request.Request(url, data=body, headers={} if content_type is None else {"Content-Type": content_type})
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.load(response)

    def queue(self):
        result = self.api("queue")["queue"]
        assert result["paused"] is True, "Synthetic jobs must stay paused"
        return result["slots"]

    def add(self, number):
        # Nonexistent groups/message IDs, a single synthetic segment, no trackers,
        # no server credentials and no media content. The queue remains paused.
        nzb = ('<?xml version="1.0" encoding="UTF-8"?>\n<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">'
            '<file poster="synthetic@example.invalid" date="1794355200" subject="Synthetic-' + str(number) + '.bin yEnc (1/1)">'
            '<groups><group>alt.binaries.synthetic.invalid</group></groups><segments>'
            '<segment bytes="1048576" number="1">synthetic-edition-' + str(number) + '@example.invalid</segment>'
            '</segments></file></nzb>').encode()
        boundary = "synthetic-edition-" + uuid.uuid4().hex
        body = ('--' + boundary + '\r\nContent-Disposition: form-data; name="name"; filename="SyntheticOpaque' + str(number) + '.nzb"\r\n'
                'Content-Type: application/x-nzb\r\n\r\n').encode() + nzb + ('\r\n--' + boundary + '--\r\n').encode()
        result = self.api("addfile", body, "multipart/form-data; boundary=" + boundary,
                          cat="movies", priority=-2, nzbname="SyntheticOpaque" + str(number))
        assert result["status"] is True and len(result["nzo_ids"]) == 1, result
        identity = result["nzo_ids"][0]
        assert isinstance(identity, str) and identity and identity not in self.created
        self.created.append(identity)
        return identity

    def restart(self):
        assert p.PROCESS is None, "Stop the temporary Radarr process before restarting its client"
        logfile = self.root / "logs" / "sabnzbd.log"
        starts = logfile.read_text().count("Starting SABnzbd.py-")
        assert starts > 0
        self.api("restart")
        for _ in range(45):
            time.sleep(1)
            try:
                if logfile.read_text().count("Starting SABnzbd.py-") > starts:
                    self.queue()
                    print("PASS: real SABnzbd process restarted; a new startup marker and paused queue are verified", flush=True)
                    return
            except OSError:
                pass
        raise AssertionError("Disposable SAB process did not restart and restore its paused queue")

    def cleanup(self):
        # Only IDs uploaded by this script; never purge an entire client queue.
        for identity in self.created:
            self.api("queue", name="delete", value=identity, del_files=1)
            self.api("history", name="delete", value=identity, del_files=1)


def seed(identities, secondary):
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary,))
        settings = {"host": "radarr-edition-sab", "port": 8080, "apiKey": SAB_KEY, "movieCategory": "movies"}
        db.execute('''INSERT INTO "DownloadClients" ("Id","Enable","Name","Implementation","Settings","ConfigContract","Priority",
            "RemoveCompletedDownloads","RemoveFailedDownloads","Tags")
            VALUES (?,TRUE,'Synthetic Real SAB','Sabnzbd',?,'SabnzbdSettings',1,FALSE,FALSE,'[]')''', (CLIENT_ID, json.dumps(settings)))
        for eid, identity in identities.items():
            title = "Synthetic.Lunar.Voyage.2026." + {1: "Theatrical.IMAX", 2: "Directors.Cut.3D"}[eid] + ".1080p.BluRay-SyntheticGroup"
            edition = next(row for row in p.rows("MovieEditions") if row["Id"] == eid)
            data = {"downloadClientId": str(CLIENT_ID), "movieEditionName": edition["Name"], "indexer": "Synthetic Indexer",
                "publishedDate": "2026-11-11T00:00:00Z", "size": "1048576", "protocol": "1", "releaseSource": "Rss"}
            release = {"title": title, "guid": "synthetic-real-sab-" + str(eid), "indexer": "Synthetic Indexer", "indexerId": 77,
                "size": 1048576, "downloadProtocol": "usenet", "publishDate": "2026-11-11T00:00:00Z"}
            quality = json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}})
            db.execute('''INSERT INTO "History" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Quality","Date","EventType","Data","Languages","DownloadId")
                VALUES (1,?,?,?,?,?,1,?,'[1]',?)''', (eid, edition["RuleRevision"], title, quality, "2026-11-11T00:00:00Z", json.dumps(data), identity))
            db.execute('''INSERT INTO "DownloadHistory" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Date","EventType","DownloadId","Protocol","IndexerId","DownloadClientId","Release","Data")
                VALUES (1,?,?,?,'2026-11-11T00:00:00Z',1,?,1,77,?,?,?)''',
                (eid, edition["RuleRevision"], title, identity, CLIENT_ID, json.dumps(release), json.dumps(data)))


def queue(identities, expected):
    p.command("RefreshMonitoredDownloads")
    records = p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"]
    relevant = [row for row in records if row.get("downloadClientId") == CLIENT_ID and row["downloadId"] in identities.values()]
    assert {row["movieEditionId"] for row in relevant} == set(expected), relevant
    for row in relevant:
        eid = row["movieEditionId"]
        assert row["downloadId"] == identities[eid] and row["movieId"] == 1 and row["trackedDownloadState"] == "downloading", row
        assert row["status"] == "paused", row
        assert row["customFormatScore"] == row["editionCustomFormatScore"] == {1: 11, 2: 77}[eid], row
    return {row["movieEditionId"]: row for row in relevant}


def run(postgres, client, remove_edition):
    assert not client.queue(), "Use a fresh empty disposable SAB queue"
    config = client.api("get_config")["config"]
    assert not client.api("get_config", section="servers")["config"].get("servers"), "Never configure or contact a Usenet server"
    assert config["misc"]["complete_dir"].startswith("/config/")
    print("Real isolated SAB version: " + client.api("version")["version"], flush=True)
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    identities = {eid: client.add(eid) for eid in [1, 2]}
    assert {row["nzo_id"] for row in client.queue()} == set(identities.values())
    assert all(row["filename"].startswith("SyntheticOpaque") for row in client.queue())
    p.stop()
    seed(identities, secondary)
    p.start()
    media = p.snapshot()
    first = queue(identities, [1, 2])
    assert first[1]["id"] != first[2]["id"]
    history = {table: p.rows(table) for table in ["History", "DownloadHistory"]}
    p.stop()
    p.start()
    restored = queue(identities, [1, 2])
    assert {eid: row["id"] for eid, row in restored.items()} == {eid: row["id"] for eid, row in first.items()}
    assert p.snapshot() == media and history == {table: p.rows(table) for table in history}
    print("PASS: two opaque real SAB jobs retain independent editions/scores/queue IDs and saved intent through actual Radarr restart", flush=True)

    target = p.api("api/v3/movieedition/2")
    body = c.monitoring_request(2, False)
    body.pop("monitored")
    p.api("api/v3/movieedition/2/definition", {**body, "name": target["name"], "attributes": target["attributes"], "matchRules": ["SyntheticNeverMatchesTheseJobs$"]})
    p.stop()
    client.restart()
    assert {row["nzo_id"] for row in client.queue()} == set(identities.values())
    p.start()
    restored = queue(identities, [1, 2])
    assert {eid: row["id"] for eid, row in restored.items()} == {eid: row["id"] for eid, row in first.items()}
    assert p.snapshot() == media and history == {table: p.rows(table) for table in history}
    print("PASS: real SAB and Radarr process restarts preserve both paused jobs and recorded assignments despite a rule edit", flush=True)

    remaining = 3 - remove_edition
    p.api("api/v3/queue/" + str(restored[remove_edition]["id"]) + "?removeFromClient=true&blocklist=false", method="DELETE")
    queue(identities, [remaining])
    assert {row["nzo_id"] for row in client.queue()} == {identities[remaining]}
    settled = [row for row in p.rows("DownloadHistory") if row["EventType"] == 4]
    assert len(settled) == 1 and settled[0]["MovieEditionId"] == remove_edition and settled[0]["DownloadClientId"] == CLIENT_ID and settled[0]["DownloadId"] == identities[remove_edition], settled
    assert p.snapshot() == media
    p.stop()
    p.start()
    queue(identities, [remaining])
    p.command("RescanMovie", movieId=1)
    assert p.snapshot() == media
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: actual queue removal reaches only the selected real SAB job/edition and remains isolated across restart; both media/subtitle pairs survive with no broad search", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--postgres", action="store_true")
    parser.add_argument("--sab-config", type=Path, required=True)
    parser.add_argument("--remove-edition", type=int, choices=[1, 2], default=1)
    client = None
    try:
        arguments = parser.parse_args()
        client = DisposableSab(arguments.sab_config)
        run(arguments.postgres, client, arguments.remove_edition)
    finally:
        p.stop()
        try:
            if client:
                client.cleanup()
        finally:
            p.cleanup()
