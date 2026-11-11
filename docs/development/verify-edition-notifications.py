"""Capture real edition webhooks and notification scripts using disposable data.

Run inside the disposable SDK runner, externally offline, with the same tools
as verify-phase3.py. All HTTP notifications go to a loopback capture server;
the generated notification script only records its environment under ROOT.
This never enables custom import scripts or automatic edition acquisition.
"""

import importlib.util
import json
from pathlib import Path
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("edition_clients", Path(__file__).with_name("verify-edition-client-recovery.py"))
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
p = c.p
WEBHOOKS = []
SERVER = None


class Capture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        assert self.path == "/synthetic-notifications"
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        WEBHOOKS.append(payload)
        self.send_response(200)
        self.end_headers()


def scripts():
    path = p.ROOT / "notification-environments.jsonl"
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def events():
    return len(WEBHOOKS), len(scripts())


def expect(events_before, kind):
    webhook_start, script_start = events_before
    hooks = [row for row in WEBHOOKS[webhook_start:] if row["eventType"] == kind]
    captures = [row for row in scripts()[script_start:] if row["radarr_eventtype"] == kind]
    assert len(hooks) == len(captures) == 1, (kind, hooks, captures)
    return hooks[0], captures[0]


def check_edition(actual, edition):
    assert actual["id"] == edition["id"] and actual["movieId"] == 1 and actual["isResolved"] is True, actual
    for field in ["name", "editionKey", "qualityProfileId", "ruleRevision", "attributes"]:
        assert actual[field] == edition[field], (field, actual, edition)


def run(postgres):
    global SERVER
    if postgres:
        p.DATABASE.enable_postgres()
    p.run()
    secondary = c.configure_profiles()["id"]
    SERVER = ThreadingHTTPServer(("127.0.0.1", 0), Capture)
    threading.Thread(target=SERVER.serve_forever, daemon=True).start()
    capture_script = p.ROOT / "capture-notification.py"
    capture_script.write_text("#!/usr/bin/python3\nimport json, os\nfrom pathlib import Path\n"
        "with Path(__file__).with_name('notification-environments.jsonl').open('a') as output:\n"
        "    output.write(json.dumps({k.lower(): v for k, v in os.environ.items() if k.lower().startswith('radarr_')}) + '\\n')\n")
    capture_script.chmod(0o700)
    p.stop()
    with p.database() as db:
        db.execute('UPDATE "MovieEditions" SET "QualityProfileId"=?,"RuleRevision"=12 WHERE "Id"=2', (secondary,))
        for identity, implementation, settings in [
            (61, "Webhook", {"url": "http://127.0.0.1:" + str(SERVER.server_port) + "/synthetic-notifications", "method": 1, "headers": []}),
            (62, "CustomScript", {"path": str(capture_script)}),
        ]:
            db.execute('''INSERT INTO "Notifications" ("Id","Name","Implementation","ConfigContract","Settings","Tags",
                "OnGrab","OnDownload","OnUpgrade","OnRename","OnMovieFileDelete","OnMovieFileDeleteForUpgrade",
                "OnMovieAdded","OnMovieDelete","OnHealthIssue","IncludeHealthWarnings","OnHealthRestored","OnApplicationUpdate","OnManualInteractionRequired")
                VALUES (?,?,?,?,?,'[]',FALSE,TRUE,TRUE,TRUE,TRUE,TRUE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE,FALSE)''',
                (identity, "Synthetic " + implementation, implementation, implementation + "Settings", json.dumps(settings)))
    p.start()
    initial = p.snapshot()
    definitions = p.api("api/v3/movieedition?movieId=1")
    editions = {row["id"]: row for row in definitions}
    for target, colour in [(1, "white"), (2, "purple")]:
        previous = p.snapshot()
        marker = events()
        p.manual(target, "SyntheticNotificationUpgrade" + str(target), colour)
        current = p.snapshot()
        assert current[3 - target] == previous[3 - target]
        imported, imported_env = expect(marker, "Download")
        deleted, deleted_env = expect(marker, "MovieFileDelete")
        check_edition(imported["movieEdition"], editions[target])
        check_edition(imported["movieFile"]["movieEdition"], editions[target])
        check_edition(imported["deletedFiles"][0]["movieEdition"], editions[target])
        check_edition(deleted["movieFile"]["movieEdition"], editions[target])
        assert imported["movieFile"]["movieEditionId"] == deleted["movieFile"]["movieEditionId"] == target
        assert imported["movieFile"]["id"] == current[target][0]
        assert imported["deletedFiles"][0]["id"] == deleted["movieFile"]["id"] == previous[target][0]
        assert imported["customFormatInfo"]["customFormatScore"] == {1: 11, 2: 77}[target]
        for env in [imported_env, deleted_env]:
            assert env["radarr_movieedition_id"] == str(target)
            assert env["radarr_movieedition_name"] == editions[target]["name"]
            check_edition(json.loads(env["radarr_movieedition"]), editions[target])
        replaced = json.loads(imported_env["radarr_deletedmoviefile_editions"])
        assert len(replaced) == 1 and replaced[0]["movieFileId"] == previous[target][0] and replaced[0]["movieEditionId"] == target
        assert p.api("api/v3/movie/1")["movieFileId"] == current[1][0]
        assert p.api("api/v3/movie/1")["qualityProfileId"] == 1
    print("PASS: real import/upgrade/delete webhooks and notification-script environments identify only the assigned edition, including replaced files and independent scores", flush=True)

    before = p.snapshot()
    naming = p.api("api/v3/config/naming")
    naming["standardMovieFormat"] += " notification"
    p.api("api/v3/config/naming/" + str(naming["id"]), naming, "PUT")
    marker = events()
    p.command("RenameMovie", movieIds=[1])
    renamed = p.snapshot()
    hook, env = expect(marker, "Rename")
    records = hook["renamedMovieFiles"]
    assert {row["movieEditionId"] for row in records} == {1, 2}
    for record in records:
        eid = record["movieEditionId"]
        check_edition(record["movieEdition"], editions[eid])
        assert record["id"] == renamed[eid][0] and record["previousRelativePath"] == before[eid][1]
        assert renamed[eid][2] == before[eid][2] and renamed[eid][5] == before[eid][5]
    env_files = json.loads(env["radarr_moviefile_editions"])
    assert [(row["movieFileId"], row["movieEditionId"]) for row in env_files] == [(row["id"], row["movieEditionId"]) for row in records]
    assert env["radarr_moviefile_ids"].split(",") == [str(row["movieFileId"]) for row in env_files]
    assert env["radarr_moviefile_movieeditionids"].split(",") == [str(row["movieEditionId"]) for row in env_files]
    p.stop()
    p.start()
    p.command("RescanMovie", movieId=1)
    p.command("Housekeeping")
    assert p.snapshot() == renamed
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    assert {eid: row[2] for eid, row in initial.items()} != {eid: row[2] for eid, row in renamed.items()}
    print("PASS: one real rename carries separate combined-edition metadata and aligned file IDs to both providers; both video/subtitle pairs survive restart/scan/housekeeping without acquisition", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise ValueError("Only --postgres is supported")
        run(sys.argv[1:] == ["--postgres"])
    finally:
        p.stop()
        try:
            if SERVER:
                SERVER.shutdown()
                SERVER.server_close()
        finally:
            p.cleanup()
