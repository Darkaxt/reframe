"""Claude-only ZIP archive/restore around the installed exact-retirement helper.

Run with Python -I. No Codex database access, shell, wildcard deletion or process stop.
The helper and its hash are installation bindings, never data from a chat/archive.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import sys
import zipfile


def digest_stream(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        value.update(block)
    return value.hexdigest()


def digest(file):
    with Path(file).open("rb") as stream:
        return digest_stream(stream)


def save(file, value):
    with Path(file).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())


def helper():
    file = Path(os.environ.get("REFRAME_HISTORY_HELPER", ""))
    expected = os.environ.get("REFRAME_HISTORY_HELPER_SHA256", "")
    if not file.is_file() or not expected or digest(file) != expected:
        raise ValueError("Configured thread-cleanup helper is missing or changed; review its installation binding")
    spec = importlib.util.spec_from_file_location("reframe_retirement", file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def inactive(module, home, tid):
    module.exact_id(tid)
    if tid in module.live_claude_ids(home):
        # The installed helper only defines CleanupError; ActiveClaudeError is optional.
        raise getattr(module, "ActiveClaudeError", module.CleanupError)(
            "Chat is running or has a live registration; end it before retrying")


def select_rows(file, tid):
    if not file.exists():
        return []
    if file.name == "history.jsonl":
        selected = []
        with file.open("rb") as stream:
            for line in stream:
                record = json.loads(line)
                if not isinstance(record, dict):
                    raise ValueError("Unsupported shared history format")
                if record.get("sessionId") == tid:
                    selected.append(record)
        return selected
    data = json.loads(file.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or not isinstance(data.get("entries"), list):
        raise ValueError("Unsupported shared session index")
    return [row for row in data["entries"] if row.get("sessionId") == tid]


def relative(file, home):
    return Path(file).relative_to(home).as_posix()


def allowed_member(name, tid):
    p = PurePosixPath(name)
    parts = p.parts
    if not parts or p.is_absolute() or any(x in ("..", ".") or ":" in x or "\\" in x for x in parts):
        return False
    if len(parts) >= 3 and parts[0] == "projects":
        return (len(parts) == 3 and parts[2] == tid + ".jsonl") or parts[2] == tid
    return (len(parts) >= 3 and parts[0] in ("file-history", "session-env", "tasks") and parts[1] == tid) or name in (
        "debug/" + tid + ".txt", "security_warnings_state_" + tid + ".json")


def verify_archive(file, home, tid):
    with zipfile.ZipFile(file) as archive:
        if archive.getinfo("manifest.json").file_size > 16 * 1024 * 1024:
            raise ValueError("Archive manifest is too large")
        manifest = json.loads(archive.read("manifest.json"))
        if manifest.get("schema") != "reframe.claude-archive.v1" or manifest.get("id") != tid or Path(manifest["home"]).resolve() != home.resolve():
            raise ValueError("Archive belongs to another chat/home or uses an unsupported format")
        names = ["manifest.json"] + ["payload/" + f["relative"] for f in manifest["files"]]
        if sorted(names) != sorted(archive.namelist()) or len(set(names)) != len(names):
            raise ValueError("Archive contains extra or duplicate entries")
        projects = set()
        for member in manifest["files"]:
            name = member["relative"]
            if not allowed_member(name, tid):
                raise ValueError("Archive member escapes exact chat artifact scope")
            if name.startswith("projects/"):
                projects.add(PurePosixPath(name).parts[1])
            entry = archive.getinfo("payload/" + name)
            if entry.file_size != member["bytes"] or entry.external_attr >> 16 & 0o170000 == 0o120000:
                raise ValueError("Archive member size/type mismatch")
            with archive.open(entry) as stream:
                if digest_stream(stream) != member["sha256"]:
                    raise ValueError("Archive member digest mismatch")
        if len(projects) != 1:
            raise ValueError("Archive must contain exactly one project history")
        project = next(iter(projects))
        for index in manifest["indexes"]:
            if index["relative"] not in ("history.jsonl", f"projects/{project}/sessions-index.json"):
                raise ValueError("Archive shared index is outside this chat")
            if any(not isinstance(row, dict) or row.get("sessionId") != tid for row in index["rows"]):
                raise ValueError("Archive contains another chat's index rows")
        return manifest


def prepare(module, home, tid, operation):
    # Planning only reads exact artifacts. The retirement helper owns the fresh
    # activity check immediately before any source/index mutation.
    packet = module.plan("claude", home, tid)
    save(operation / "plan.json", packet)
    return {"plan": str(operation / "plan.json"), "sha256": digest(operation / "plan.json"),
            "id": tid, "files": len(packet["snapshot"]["files"]),
            "bytes": sum(f["bytes"] for f in packet["snapshot"]["files"])}


def load_plan(module, home, tid, file, expected):
    if digest(file) != expected:
        raise ValueError("Reviewed retirement plan changed")
    packet = json.loads(Path(file).read_text(encoding="utf-8"))
    if packet.get("schema") != "thread_cleanup.plan.v1" or packet.get("system") != "claude" or packet["snapshot"]["id"] != tid or Path(packet["home"]).resolve() != home.resolve():
        raise ValueError("Retirement plan does not match exact chat/home")
    return packet


def archive_chat(module, home, tid, operation, file, expected, metadata):
    packet = load_plan(module, home, tid, file, expected)
    snapshot = packet["snapshot"]
    manifest = {"schema": "reframe.claude-archive.v1", "id": tid, "home": str(home),
                "metadata": metadata, "files": [], "indexes": []}
    # Source files remain intact until the complete archive has been independently read back.
    archive_path = operation / (tid + ".zip")
    for member in snapshot["files"]:
        name = relative(member["path"], home)
        if not allowed_member(name, tid):
            raise ValueError("Retirement helper returned an unsupported artifact")
        manifest["files"].append({"relative": name, "bytes": member["bytes"], "sha256": member["sha256"]})
    for index in snapshot["shared_indexes"]:
        manifest["indexes"].append({"relative": relative(index, home), "rows": select_rows(Path(index), tid)})
    with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9, allowZip64=True) as archive:
        for member in snapshot["files"]:
            archive.write(member["path"], "payload/" + relative(member["path"], home))
        archive.writestr("manifest.json", json.dumps(manifest, indent=2))
    with archive_path.open("r+b") as stream:
        os.fsync(stream.fileno())
    # Verified once, after it is written and before any live file is removed.
    verify_archive(archive_path, home, tid)
    sha256 = digest(archive_path)
    save(operation / "verified.json", {"archive": str(archive_path), "sha256": sha256,
                                       "bytes": archive_path.stat().st_size, "manifest": manifest})
    # The installed helper rechecks fresh process references, complete snapshot and exact file identities,
    # prunes only this chat's shared rows under an exclusive handle, and refuses changed/new members.
    result = module.delete_claude(packet)
    save(operation / "retirement.json", result)
    if not result["complete"]:
        raise ValueError("ZIP is verified but retirement is partial; recovery and exact residual receipt are retained")
    return {"complete": True, "archive": str(archive_path), "sha256": sha256, "id": tid}


def exclusive_index(module, file, update):
    """Same exclusive-handle contract as installed prune_index; keep concurrent unrelated rows."""
    import msvcrt
    fs = module.filesystem()
    file.parent.mkdir(parents=True, exist_ok=True)
    fs.check_chain(file.parent)
    if file.exists():
        fs.check_chain(file)
    handle = fs.kernel.CreateFileW(str(file), 0xC0000000, 0, None, 4, 0x00200000, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    fd = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
    with os.fdopen(fd, "r+b") as stream:
        original = stream.read()
        updated = update(original)
        try:
            stream.seek(0)
            stream.write(updated)
            stream.truncate()
            stream.flush()
            os.fsync(stream.fileno())
        except BaseException:
            stream.seek(0)
            stream.write(original)
            stream.truncate()
            stream.flush()
            os.fsync(stream.fileno())
            raise


def restore(module, home, tid, file, expected):
    if digest(file) != expected:
        raise ValueError("Archive digest differs from its recorded activation")
    inactive(module, home, tid)
    manifest = verify_archive(file, home, tid)
    fs = module.filesystem()
    # Refuse all conflicting files before writing any member. Identical files allow recovery after interruption.
    for member in manifest["files"]:
        target = home / member["relative"]
        fs.check_chain(target.parent)
        if target.exists() and (not target.is_file() or digest(target) != member["sha256"]):
            raise ValueError("Restore conflicts with an existing artifact; nothing overwritten")
    with zipfile.ZipFile(file) as archive:
        for member in manifest["files"]:
            target = home / member["relative"]
            if target.exists():
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            fs.check_chain(target.parent)
            with target.open("xb") as output, archive.open("payload/" + member["relative"]) as source:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    output.write(block)
                output.flush()
                os.fsync(output.fileno())
            if digest(target) != member["sha256"]:
                raise ValueError("Restored file failed readback; archive retained")
    for index in manifest["indexes"]:
        rows = index["rows"]
        def merge(original):
            if index["relative"] == "history.jsonl":
                current = [json.loads(line) for line in original.splitlines()]
                known = {json.dumps(row, sort_keys=True) for row in current}
                extra = [row for row in rows if json.dumps(row, sort_keys=True) not in known]
                return original + (b"\n" if original and not original.endswith(b"\n") else b"") + b"".join((json.dumps(row) + "\n").encode() for row in extra)
            data = json.loads(original) if original else {"version": 1, "entries": []}
            if not isinstance(data, dict) or not isinstance(data.get("entries"), list):
                raise ValueError("Unsupported restore index format")
            existing = [row for row in data["entries"] if row.get("sessionId") == tid]
            if existing and existing != rows:
                raise ValueError("Restore session-index conflict; unrelated rows preserved")
            if not existing:
                data["entries"].extend(rows)
            return (json.dumps(data, indent=2) + "\n").encode()
        exclusive_index(module, home / index["relative"], merge)
    return {"complete": True, "id": tid, "metadata": manifest["metadata"]}


def recover(home, root, module):
    entries, warnings, restored = [], [], []
    operations = root / "operations"
    if not operations.exists():
        return {"complete": True, "entries": [], "warnings": [], "restored": []}
    module.filesystem().check_chain(operations)
    for operation in operations.iterdir():
        verified = operation / "verified.json"
        if not verified.is_file():
            continue
        try:
            module.filesystem().check_chain(operation)
            record = json.loads(verified.read_text(encoding="utf-8"))
            tid = record["manifest"]["id"]
            if any(json.loads(file.read_text(encoding="utf-8")).get("complete") for file in operation.glob("restored-*.json")):
                restored.append(tid)
                continue
            file = Path(record["archive"])
            # Startup only checks the binding and size recorded when the ZIP was verified; restore re-verifies
            # its digest and contents before writing anything.
            if (file.resolve().parent != operation.resolve() or file.name != tid + ".zip" or not file.is_file()
                    or ("bytes" in record and file.stat().st_size != record["bytes"])):
                raise ValueError("Recovery archive binding changed")
            manifest = record["manifest"]
            partial = any((home / member["relative"]).exists() for member in manifest["files"])
            meta = manifest["metadata"]
            entries.append({"id": tid, "label": ("Recovery: " if partial else "") + str(meta["label"]),
                            "cwd": str(meta["cwd"]), "provider": meta.get("provider"), "partial": partial,
                            "archive": str(file), "sha256": record["sha256"], "operation": str(operation)})
        except Exception as error:
            warnings.append({"operation": str(operation), "error": str(error)})
    return {"complete": True, "entries": entries, "warnings": warnings, "restored": restored}


def census_once(module, home):
    """One live-process census for a whole batch, taken immediately before the first mutation.

    The helper's delete_claude takes its own census per chat (a PowerShell CIM query each); within one batch
    the same answer is reused so N chats cost one query, not N."""
    ids = list(module.live_claude_ids(home))
    module.live_claude_ids = lambda _home: ids
    return ids


def failure(module, tid, error):
    active = isinstance(error, getattr(module, "ActiveClaudeError", ())) or "live registration" in str(error)
    return {"id": tid, "ok": False, "error": str(error), "code": "chat_active" if active else None}


def checked_operation(module, operation):
    operation = Path(operation).resolve()
    if operation.drive.casefold() == "c:":
        raise ValueError("Choose a non-C archive/evidence folder")
    module.filesystem().check_chain(operation.parent)
    operation.mkdir(parents=True, exist_ok=True)
    module.filesystem().check_chain(operation)
    return operation


def prepare_batch(module, home, jobs):
    results = []
    for job in jobs:
        try:
            operation = checked_operation(module, job["operation"])
            results.append({"ok": True, "operation": str(operation), **prepare(module, home, job["id"], operation)})
        except Exception as error:
            results.append(failure(module, job.get("id"), error))
    return {"complete": True, "results": results}


def retire_batch(module, home, jobs):
    """Archive or delete prepared chats. Nothing is removed unless its chat is not live (one census per batch),
    its artifacts are unchanged since planning, and - when archiving - its ZIP verified."""
    census_once(module, home)
    results = []
    for job in jobs:
        tid = job["id"]
        try:
            operation = checked_operation(module, job["operation"])
            if job["mode"] == "archive":
                result = archive_chat(module, home, tid, operation, job["plan"], job["sha256"], job["metadata"])
            elif job["mode"] == "delete":
                result = module.delete_claude(load_plan(module, home, tid, job["plan"], job["sha256"]))
                save(operation / "retirement.json", result)
                if not result["complete"]:
                    raise ValueError("Deletion is partial; exact residual receipt retained at " + str(operation))
                result = {"complete": True, "id": tid}
            else:
                raise ValueError("Unknown retirement mode")
            results.append({"ok": True, **result})
        except Exception as error:
            results.append(failure(module, tid, error))
    return {"complete": True, "results": results}


def main():
    parser = argparse.ArgumentParser()
    # prepare/retire read {"jobs": [...]} from stdin: one process for any number of chats.
    parser.add_argument("command", choices=("recover", "prepare", "retire", "restore"))
    parser.add_argument("--home", required=True)
    parser.add_argument("--id")
    parser.add_argument("--operation")
    parser.add_argument("--sha256")
    parser.add_argument("--archive")
    args = parser.parse_args()
    module = None
    try:
        module = helper()
        home = Path(args.home).resolve()
        if os.name != "nt":
            raise ValueError("Managed archive/delete currently require Windows exact-handle support")
        if args.command in ("prepare", "retire"):
            jobs = json.load(sys.stdin)["jobs"]
            result = (prepare_batch if args.command == "prepare" else retire_batch)(module, home, jobs)
        else:
            operation = Path(args.operation).resolve()
            if operation.drive.casefold() == "c:":
                raise ValueError("Choose a non-C archive/evidence folder")
            module.filesystem().check_chain(operation)
            if args.command == "recover":
                result = recover(home, operation, module)
            else:
                result = restore(module, home, args.id, args.archive, args.sha256)
                save(operation / ("restored-" + str(os.getpid()) + ".json"), result)
        print(json.dumps({"ok": result.get("complete", True), "result": result}))
        return 0 if result.get("complete", True) else 1
    except Exception as error:
        print(json.dumps({"ok": False, "error": str(error),
                          "code": "chat_active" if isinstance(error, getattr(module, "ActiveClaudeError", ())) else None}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
