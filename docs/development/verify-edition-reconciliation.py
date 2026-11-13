"""Verify restart/housekeeping recovery with only disposable databases and media.

Run in the externally offline SDK runner; optional --postgres. Acceptance and
malformed history are explicit offline synthetic fixtures, not client inference.
"""

from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import sys
import time

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("journal", Path(__file__).with_name("verify-edition-journal.py"))
j = importlib.util.module_from_spec(spec)
spec.loader.exec_module(j)
p = j.p


def insert(db, table, row):
    assert all(key.isalpha() for key in row)
    db.execute('INSERT INTO "' + table + '" (' + ','.join('"' + key + '"' for key in row)
               + ') VALUES (' + ','.join('?' for _ in row) + ')', tuple(row.values()))


def wait_states(expected):
    for _ in range(150):
        rows = p.rows("MovieEditionSubmissions")
        if all(next(row["State"] for row in rows if row["OperationId"] == operation) == state for operation, state in expected.items()):
            return rows
        time.sleep(0.1)
    raise AssertionError(("Recovery did not reach expected states", expected, rows))


def run(postgres):
    j.run(postgres)
    media = p.snapshot()
    p.stop()
    original = p.rows("MovieEditionSubmissions")[0]
    primary = next(row for row in p.rows("MovieEditions") if row["Id"] == 1)
    operations = {name: "00000000-0000-4000-8000-0000000000" + suffix for name, suffix in
                  (("conflict", "41"), ("primary", "42"), ("prepared", "43"), ("incomplete", "44"), ("other", "45"))}
    with p.database() as db:
        for edition_id, label in ((3, "Prepared"), (4, "Incomplete"), (5, "Recovered")):
            insert(db, "MovieEditions", {"Id": edition_id, "MovieId": 1, "EditionKey": "synthetic-recovery-" + label.lower(),
                "Name": "Synthetic " + label + " 3D", "Monitored": False, "QualityProfileId": primary["QualityProfileId"],
                "RuleRevision": 1, "Attributes": json.dumps({"cut": "directors-cut", "stereoscopic": "3d", "custom-label": label}), "MatchRules": '[]'})
    targets = {row["Id"]: row for row in p.rows("MovieEditions")}
    with p.database() as db:
        for name, edition_id, state in (("incomplete", 4, 1), ("primary", 1, 1), ("prepared", 3, 0), ("other", 5, 1)):
            target = targets[edition_id]
            row = {key: value for key, value in original.items() if key != "Id"}
            row.update(OperationId=operations[name], MovieEditionId=edition_id, EditionName=target["Name"], EditionAttributes=target["Attributes"],
                       EditionRuleRevision=target["RuleRevision"], QualityProfileId=target["QualityProfileId"], MovieFileId=target["MovieFileId"],
                       State=state, DownloadId=None if state == 0 else "synthetic-recovery-" + name)
            for field in ("HistoryData", "DownloadHistoryData"):
                data = json.loads(row[field])
                data["movieEditionName"] = target["Name"]
                row[field] = json.dumps(data)
            if name == "primary":
                release = json.loads(row["Release"])
                release["title"] = "Synthetic.Lunar.Voyage.2026.Theatrical.IMAX.1080p.BluRay-SyntheticGroup"
                row["Release"] = json.dumps(release)
            if name == "incomplete":
                row["HistoryData"] = None
            insert(db, "MovieEditionSubmissions", row)
        insert(db, "DownloadHistory", {"MovieId": 1, "MovieEditionId": original["MovieEditionId"], "EditionRuleRevision": original["EditionRuleRevision"],
            "EditionOperationId": operations["conflict"], "DownloadClientId": original["DownloadClientId"], "DownloadId": original["DownloadId"],
            "EventType": 2, "Date": datetime.now(timezone.utc).isoformat(), "SourceTitle": "Synthetic.Opaque.Completed-SyntheticGroup",
            "Protocol": 1, "IndexerId": 0, "Data": json.dumps({"movieEditionName": original["EditionName"]})})
    before = {row["OperationId"]: row for row in p.rows("MovieEditionSubmissions")}
    p.start()
    expected = {operations[name]: state for name, state in (("primary", 2), ("other", 2), ("prepared", 0), ("incomplete", 1), ("conflict", 2))}
    wait_states(expected)
    p.command("Housekeeping")
    first = {row["OperationId"]: row for row in p.rows("MovieEditionSubmissions")}
    for name in ("prepared", "incomplete", "conflict"):
        assert first[operations[name]] == before[operations[name]]
    assert p.snapshot() == media
    assert len([row for row in p.rows("History") if row.get("EditionOperationId") == operations["primary"]]) == 1
    assert len([row for row in p.rows("History") if row.get("EditionOperationId") == operations["other"]]) == 1
    print("PASS: startup recovers later accepted operations past conflicting/incomplete evidence, preserving unknown intent, both media pairs and saved payloads without client IO", flush=True)

    primary_download = next(row for row in p.rows("DownloadHistory") if row.get("EditionOperationId") == operations["primary"] and row["EventType"] == 1)
    p.stop()
    with p.database() as db:
        db.execute('DELETE FROM "History" WHERE "EditionOperationId"=? AND "EventType"=1', (operations["primary"],))
    p.start()
    for _ in range(150):
        if any(row.get("EditionOperationId") == operations["primary"] for row in p.rows("History")):
            break
        time.sleep(0.1)
    else:
        raise AssertionError("Startup did not repair missing movie-history stream")
    assert next(row for row in p.rows("DownloadHistory") if row["Id"] == primary_download["Id"]) == primary_download
    stable = p.rows("MovieEditionSubmissions")
    p.command("Housekeeping")
    assert p.rows("MovieEditionSubmissions") == stable and p.snapshot() == media
    p.stop()
    p.start()
    p.command("Housekeeping")
    assert p.rows("MovieEditionSubmissions") == stable and p.snapshot() == media
    print("PASS: restart repairs one missing stream while preserving the other grab ID; healthy subsequent restart/housekeeping retains journal timestamps, operation IDs and media", flush=True)

    p.stop()
    record = next(row for row in stable if row["OperationId"] == operations["primary"])
    with p.database() as db:
        insert(db, "DownloadHistory", {"MovieId": 1, "MovieEditionId": 1, "EditionRuleRevision": record["EditionRuleRevision"],
            "EditionOperationId": operations["primary"], "DownloadClientId": record["DownloadClientId"], "DownloadId": record["DownloadId"],
            "EventType": 2, "Date": datetime.now(timezone.utc).isoformat(), "SourceTitle": "Synthetic.Opaque.Completed-SyntheticGroup",
            "Protocol": 1, "IndexerId": 0, "Data": json.dumps({"movieEditionName": record["EditionName"]})})
    p.start()
    wait_states({**expected, operations["primary"]: 3})
    assert p.snapshot() == media
    assert not any(command["name"] == "MoviesSearch" for command in p.api("api/v3/command"))
    print("PASS: valid saved terminal evidence settles its operation after restart while contradictory evidence and prepared uncertainty remain unresolved, without acquisition or retry", flush=True)


if __name__ == "__main__":
    try:
        if sys.argv[1:] not in ([], ["--postgres"]):
            raise ValueError("Only --postgres is supported")
        run("--postgres" in sys.argv[1:])
    finally:
        p.cleanup()
