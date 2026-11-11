"""Real paused qBittorrent jobs against disposable edition configurations.

Requires two dedicated externally isolated clients radarr-edition-qbit{1,2}:8080,
owned configuration subdirectories 1/2 and .edition-test-owned below --client-root.
Peer discovery must be disabled. Uploads a tiny private trackerless synthetic
torrent directly; Radarr acquisition remains disabled throughout this check.
"""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
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


def bencode(value):
    if isinstance(value, int):
        return b"i" + str(value).encode() + b"e"
    if isinstance(value, bytes):
        return str(len(value)).encode() + b":" + value
    if isinstance(value, dict):
        return b"d" + b"".join(bencode(key) + bencode(value[key]) for key in sorted(value)) + b"e"
    raise TypeError(type(value))


class DisposableQbit:
    def __init__(self, root, number):
        self.root = root.resolve()
        assert self.root.is_relative_to(p.RESULTS.resolve()) and self.root.name.startswith("real-qbit-")
        assert (self.root / ".edition-test-owned").read_text().startswith("synthetic disposable qBittorrent clients ")
        assert number in (1, 2) and (self.root / str(number) / "qBittorrent" / "qBittorrent.conf").is_file()
        self.number = number
        self.client_id = 40 + number
        self.host = "radarr-edition-qbit" + str(number)
        self.created = []

    def api(self, path, fields=None, body=None, content_type=None):
        headers = {} if content_type is None else {"Content-Type": content_type}
        if fields is not None:
            body = urllib.parse.urlencode(fields).encode()
        request = urllib.request.Request("http://" + self.host + ":8080/api/v2/" + path, data=body, headers=headers)
        with urllib.request.urlopen(request, timeout=15) as response:
            text = response.read().decode()
        try:
            return json.loads(text)
        except ValueError:
            return text

    def queue(self):
        prefs = self.api("app/preferences")
        assert all(prefs[setting] is False for setting in ["dht", "pex", "lsd", "upnp"]), prefs
        rows = self.api("torrents/info")
        assert all(row["state"] in ["stoppedDL", "pausedDL"] and row["downloaded"] == row["uploaded"] == 0 for row in rows), rows
        assert all(row["dlspeed"] == row["upspeed"] == row["num_seeds"] == row["num_leechs"] == 0 for row in rows), rows
        for row in rows:
            trackers = self.api("torrents/trackers?" + urllib.parse.urlencode({"hash": row["hash"]}))
            assert all(tracker["url"].startswith("** [") and tracker["status"] == 0 for tracker in trackers), trackers
        return rows

    def add(self):
        # Both clients get identical metadata/hash. There is no tracker, web seed,
        # live peer or requested media. No payload file is provided to either job.
        payload = b"Synthetic edition-only test payload\n"
        info = {b"name": b"SyntheticOpaqueTorrent", b"length": len(payload), b"piece length": 16384,
                b"pieces": hashlib.sha1(payload).digest(), b"private": 1}
        identity = hashlib.sha1(bencode(info)).hexdigest()
        torrent = bencode({b"info": info, b"comment": b"Disposable edition recovery test"})
        boundary = "synthetic-edition-" + uuid.uuid4().hex
        body = b""
        for key, value in {"category": "movies", "savepath": "/config/downloads", "stopped": "true", "paused": "true", "autoTMM": "false"}.items():
            body += ("--" + boundary + '\r\nContent-Disposition: form-data; name="' + key + '"\r\n\r\n' + value + "\r\n").encode()
        body += ("--" + boundary + '\r\nContent-Disposition: form-data; name="torrents"; filename="SyntheticOpaque.torrent"\r\nContent-Type: application/x-bittorrent\r\n\r\n').encode()
        body += torrent + ("\r\n--" + boundary + "--\r\n").encode()
        if "movies" not in self.api("torrents/categories"):
            self.api("torrents/createCategory", {"category": "movies", "savePath": "/config/downloads"})
        self.created.append(identity)
        result = self.api("torrents/add", body=body, content_type="multipart/form-data; boundary=" + boundary)
        if isinstance(result, dict):
            assert result == {"added_torrent_ids": [identity], "failure_count": 0, "pending_count": 0, "success_count": 1}, result
        else:
            assert result.strip() in ("", "Ok."), result
        for _ in range(30):
            rows = self.api("torrents/info")
            assert all(row["downloaded"] == row["uploaded"] == 0 for row in rows), rows
            if rows and all(row["state"] in ["stoppedDL", "pausedDL"] for row in rows):
                rows = self.queue()
                assert len(rows) == 1 and rows[0]["hash"] == identity and rows[0]["name"] == "SyntheticOpaqueTorrent", rows
                return identity.upper()
            time.sleep(0.2)
        raise AssertionError("Synthetic torrent did not appear")

    def process(self):
        logs = self.api("log/main?normal=true&info=true&warning=true&critical=true&last_known_id=-1")
        starts = [row for row in logs if re.search(r"started\. Process ID: \d+", row["message"])]
        assert starts, logs
        return starts[-1]

    def restart(self):
        assert p.PROCESS is None, "Stop the temporary Radarr process first"
        previous = self.process()
        self.api("app/shutdown", {})
        for _ in range(45):
            time.sleep(1)
            try:
                current = self.process()
                rows = self.api("torrents/info")
                if (current["message"] != previous["message"] and current["timestamp"] >= previous["timestamp"]
                        and {row["hash"] for row in rows} == set(self.created)
                        and all(row["state"] in ["stoppedDL", "pausedDL"] for row in rows)):
                    self.queue()
                    print("PASS: real qBittorrent client " + str(self.number) + " restarted with a new process ID and restored its stopped queue", flush=True)
                    return
            except OSError:
                pass
        raise AssertionError("Disposable qBittorrent process did not restart")

    def cleanup(self):
        # Only this run's uploaded hash, scoped to its owned client.
        for identity in self.created:
            self.api("torrents/delete", {"hashes": identity, "deleteFiles": "true"})


def seed(clients, identity, secondary):
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=? WHERE "Id"=2', (secondary,))
        for client in clients:
            eid = client.number
            target = next(row for row in p.rows("MovieEditions") if row["Id"] == eid)
            settings = {"host": client.host, "port": 8080, "movieCategory": "movies"}
            db.execute('''INSERT INTO "DownloadClients" ("Id","Enable","Name","Implementation","Settings","ConfigContract","Priority","RemoveCompletedDownloads","RemoveFailedDownloads","Tags")
                VALUES (?,TRUE,?,'QBittorrent',?,'QBittorrentSettings',1,FALSE,FALSE,'[]')''',
                (client.client_id, "Synthetic Real qBittorrent " + str(eid), json.dumps(settings)))
            title = "Synthetic.Lunar.Voyage.2026." + {1: "Theatrical.IMAX", 2: "Directors.Cut.3D"}[eid] + ".1080p.BluRay-SyntheticGroup"
            data = {"downloadClientId": str(client.client_id), "movieEditionName": target["Name"], "indexer": "Synthetic Indexer",
                    "publishedDate": "2026-11-11T00:00:00Z", "size": "36", "protocol": "2", "releaseSource": "Rss"}
            release = {"title": title, "guid": "synthetic-real-qbit-" + str(eid), "infoHash": identity,
                "indexer": "Synthetic Indexer", "indexerId": 77, "size": 36, "downloadProtocol": "torrent", "publishDate": "2026-11-11T00:00:00Z"}
            quality = json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}})
            db.execute('''INSERT INTO "History" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Quality","Date","EventType","Data","Languages","DownloadId")
                VALUES (1,?,?,?,?,?,1,?,'[1]',?)''', (eid, target["RuleRevision"], title, quality, "2026-11-11T00:00:00Z", json.dumps(data), identity))
            db.execute('''INSERT INTO "DownloadHistory" ("MovieId","MovieEditionId","EditionRuleRevision","SourceTitle","Date","EventType","DownloadId","Protocol","IndexerId","DownloadClientId","Release","Data")
                VALUES (1,?,?,?,'2026-11-11T00:00:00Z',1,?,2,77,?,?,?)''', (eid, target["RuleRevision"], title, identity, client.client_id, json.dumps(release), json.dumps(data)))


def queue(identity, expected):
    p.command("RefreshMonitoredDownloads")
    rows = [r for r in p.api("api/v3/queue?page=1&pageSize=100&includeUnknownMovieItems=true")["records"] if r.get("downloadId") == identity]
    assert {r["downloadClientId"] for r in rows} == {40 + eid for eid in expected}, rows
    for row in rows:
        eid = row["downloadClientId"] - 40
        assert row["movieEditionId"] == eid and row["movieId"] == 1 and row["protocol"] == "torrent", row
        assert row["trackedDownloadState"] == "downloading" and row["status"] == "paused", row
        assert row["customFormatScore"] == row["editionCustomFormatScore"] == {1: 11, 2: 77}[eid], row
    return {row["movieEditionId"]: row for row in rows}


def run(postgres, clients, remove_edition):
    assert all(not client.queue() for client in clients), "Use fresh empty disposable clients"
    for client in clients:
        print("Real isolated client " + str(client.number) + ": " + client.api("app/version") + ", API " + client.api("app/webapiVersion"), flush=True)
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    identities = [client.add() for client in clients]
    assert identities[0] == identities[1]
    identity = identities[0]
    p.stop()
    seed(clients, identity, secondary)
    p.start()
    media = p.snapshot()
    first = queue(identity, [1, 2])
    assert first[1]["id"] != first[2]["id"]
    history = {table: p.rows(table) for table in ["History", "DownloadHistory"]}
    p.stop()
    p.start()
    restored = queue(identity, [1, 2])
    assert {eid: row["id"] for eid, row in restored.items()} == {eid: row["id"] for eid, row in first.items()}
    assert p.snapshot() == media and history == {table: p.rows(table) for table in history}
    print("PASS: identical opaque torrent hashes on two real clients retain independent edition scores/queue IDs through Radarr restart", flush=True)
    target = p.api("api/v3/movieedition/2")
    expected = c.monitoring_request(2, False)
    expected.pop("monitored")
    p.api("api/v3/movieedition/2/definition", {**expected, "name": target["name"], "attributes": target["attributes"], "matchRules": ["ChangedSyntheticGroup$"]})
    p.stop()
    for client in clients:
        client.restart()
        assert client.queue()[0]["hash"].upper() == identity
    p.start()
    restored = queue(identity, [1, 2])
    assert {eid: row["id"] for eid, row in restored.items()} == {eid: row["id"] for eid, row in first.items()}
    assert p.snapshot() == media and history == {table: p.rows(table) for table in history}
    print("PASS: both real client restarts and rule edits preserve recorded intent and stopped jobs without transferring payloads", flush=True)
    other = 3 - remove_edition
    p.api("api/v3/queue/" + str(restored[remove_edition]["id"]) + "?removeFromClient=true&blocklist=false", method="DELETE")
    queue(identity, [other])
    assert not clients[remove_edition - 1].queue() and clients[other - 1].queue()[0]["hash"].upper() == identity
    settled = [row for row in p.rows("DownloadHistory") if row["EventType"] == 4]
    assert len(settled) == 1 and settled[0]["MovieEditionId"] == remove_edition and settled[0]["DownloadClientId"] == 40 + remove_edition, settled
    assert settled[0]["EditionRuleRevision"] == 1 and settled[0]["DownloadId"] == identity, settled
    p.stop()
    p.start()
    queue(identity, [other])
    p.command("RescanMovie", movieId=1)
    assert p.snapshot() == media and not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    assert all(not client.queue() or client.queue()[0]["hash"].upper() == identity for client in clients)
    print("PASS: real torrent removal and typed settlement affect only the selected client/edition; the same hash on the other client and both media/subtitle pairs survive restart and scanning without acquisition", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--postgres", action="store_true")
    parser.add_argument("--client-root", type=Path, required=True)
    parser.add_argument("--remove-edition", type=int, choices=[1, 2], default=1)
    clients = []
    try:
        arguments = parser.parse_args()
        clients = [DisposableQbit(arguments.client_root, number) for number in [1, 2]]
        run(arguments.postgres, clients, arguments.remove_edition)
    finally:
        p.stop()
        try:
            for client in clients:
                client.cleanup()
        finally:
            p.cleanup()
