"""Run edition recovery/backup checks as UID 1000 in the isolated Docker runner.

Create a fresh UID/GID-1000 results directory below the ignored test-results
folder first, then invoke with docker exec --user 1000:1000. No real config,
media, credentials, clients or external network are used.
"""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True


def run():
    parser = argparse.ArgumentParser()
    parser.add_argument("scenario", choices=["reconciliation", "backup"])
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--postgres", action="store_true")
    options = parser.parse_args()
    assert os.getuid() == os.geteuid() == os.getgid() == 1000, "Run only as the disposable test UID/GID 1000"
    results = options.results.resolve()
    base = Path("/src/_temp/edition-test-results").resolve()
    assert results.parent == base and results.name.startswith("nonroot-"), results
    assert results.is_dir() and results.stat().st_uid == 1000 and not any(results.iterdir()), results

    spec = importlib.util.spec_from_file_location("nonroot_edition_scenario", Path(__file__).with_name("verify-edition-" + options.scenario + ".py"))
    scenario = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(scenario)
    p = scenario.p
    servers = scenario.c.SERVERS if options.scenario == "backup" else []
    p.RESULTS = results
    original_start = p.start
    starts = []

    def legacy_staging():
        return {str(path): (path.stat().st_ino, path.stat().st_uid, path.stat().st_mode, path.stat().st_mtime_ns)
                for path in (Path("/tmp/radarr_backup"), Path("/tmp/radarr_backup_restore")) if path.exists()}

    def owned_stages():
        return {str(path) for path in Path("/tmp").glob("radarr_backup_*") if path.stat().st_uid == 1000}

    legacy = legacy_staging()
    previous_stages = owned_stages()

    def start():
        original_start()
        status = Path("/proc/" + str(p.PROCESS.pid) + "/status").read_text()
        uid = next(line for line in status.splitlines() if line.startswith("Uid:")).split()[1:]
        assert uid == ["1000"] * 4, uid
        assert p.ROOT.stat().st_uid == 1000 and p.CONFIG.stat().st_uid == 1000
        starts.append(p.PROCESS.pid)

    p.start = start
    try:
        writable = [str(path) for path in [p.APP, *p.APP.rglob("*")] if os.access(path, os.W_OK)]
        assert not writable, ("The built app must be read-only to the test UID", writable)
        assert os.access(p.APP / "ffprobe", os.X_OK), "Prepare executable test ffprobe permissions before dropping privileges"
        if options.scenario == "backup":
            scenario.run(options.postgres, operation_identity=True)
        else:
            scenario.run(options.postgres)
        assert len(starts) >= 2 and len(set(starts)) == len(starts), starts
        p.stop()
        for row in p.rows("MovieFiles"):
            info = json.loads(row["MediaInfo"] or "{}")
            assert info.get("width") == 160 and info.get("height") == 90 and info.get("audioStreams"), info
        assert "[Error] VideoFileInfoReader:" not in (results / "phase3-runtime.log").read_text()
        for path in p.ROOT.rglob("*"):
            if not path.is_symlink():
                assert path.stat().st_uid == 1000, path
        assert legacy_staging() == legacy, "Legacy shared staging paths changed"
        assert owned_stages() == previous_stages, "Backup/restore left a new staging directory"
        assert not servers or all(not client.rejected for client in servers)
        print("PASS: " + options.scenario + " runs with UID/GID 1000 against a read-only application tree; all " + str(len(starts))
              + " application starts retain UID 1000 and the entire disposable configuration/library/backup tree stays owned by UID 1000", flush=True)
    finally:
        p.stop()
        try:
            for client in servers:
                client.server.shutdown()
                client.server.server_close()
        finally:
            p.cleanup()


if __name__ == "__main__":
    run()
