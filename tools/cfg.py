#!/usr/bin/env python3
"""cfg.py -- read/write BepInEx cfg settings for NoVikingLeftBehind (nvlb) and
SmoothServer (ss) on a Valheim server, with live-reload confirmation.

Stdlib only (python3). Lives on the box at /opt/modlab/tools/cfg.py.

Usage:
  cfg.py list <mod> [section]
  cfg.py get <mod> [Section] <Key>
  cfg.py set <mod> <Section.Key|Key> <value> [--server test|live] [--no-wait] [--force] [--dry-run]
  cfg.py diff <mod> [--server test|live]
  cfg.py restore <mod> [backup] [--server test|live]
  cfg.py players [--server test|live]
  cfg.py status [--server test|live]
  cfg.py search <mod> <text>

<mod> is "nvlb" (NoVikingLeftBehind) or "ss" (SmoothServer), or a few aliases.
--server defaults to "test" everywhere. "live" points at the frozen NEWWORLD
server (/opt/valheim2, container valheim-fresh) -- supported as a target but
nothing here restarts it, and callers should not use --server live until the
cut-over is declared.
"""

import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone

SERVERS = {
    "test": {"root": "/opt/valheim-test", "container": "valheim-test"},
    "live": {"root": "/opt/valheim2", "container": "valheim-fresh"},
}

MODS = {
    "nvlb": {
        "file": "Nosferatu.NoVikingLeftBehind.cfg",
        "display": "NoVikingLeftBehind",
        "names": ("nvlb", "novikingleftbehind", "no-viking-left-behind"),
    },
    "ss": {
        "file": "Nosferatu.SmoothServer.cfg",
        "display": "SmoothServer",
        "names": ("ss", "smoothserver", "smooth-server"),
    },
}

# Settings known to need a process restart even though the config watcher
# happily logs the reload -- see docs/MODULES.md in each mod repo.
RESTART_KEYS = {
    # (section, key) -- None section means "any section" (Enabled is repeated
    # in every module's own section).
    (None, "Enabled"): "Enabled toggles install Harmony patches at plugin Awake; "
                        "turning a module OFF works live, turning it back ON needs a restart",
    ("General", "Mode"): "Mode is read once at Awake (machine-local)",
    ("StatsLog", "Dir"): "StatsLog.Dir is read once at Awake, not hot-reloadable",
}

# Settings that take the new value immediately in the config file, but only
# change behaviour for objects created/spawned *after* the edit.
NEW_OBJECT_KEYS = {
    ("Trader", "Items"): "only traders spawned after this change carry the new stock; "
                          "an already-loaded Haldor keeps the old list until he respawns",
}

BACKUP_KEEP = 20


class CfgError(Exception):
    pass


class ValidationError(Exception):
    pass


# ---------------------------------------------------------------- parsing --

class Entry:
    __slots__ = ("section", "key", "value", "default", "type", "acceptable",
                 "value_range", "description", "line_index")

    def __init__(self, section, key, value, default, type_, acceptable,
                 value_range, description, line_index):
        self.section = section
        self.key = key
        self.value = value
        self.default = default
        self.type = type_
        self.acceptable = acceptable
        self.value_range = value_range
        self.description = description
        self.line_index = line_index

    def label(self):
        return "[%s] %s" % (self.section, self.key)

    def meta(self):
        bits = []
        if self.default is not None:
            bits.append("default %s" % self.default)
        if self.type:
            bits.append(self.type)
        if self.acceptable:
            bits.append("one of: " + ", ".join(self.acceptable))
        if self.value_range:
            bits.append("range %s..%s" % self.value_range)
        return ", ".join(bits)


def parse_cfg(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        lines = f.read().splitlines()

    entries = []
    sections_order = []
    section = None
    desc, type_, default, acceptable, value_range = [], None, None, None, None

    for i, line in enumerate(lines):
        s = line.strip()
        if s.startswith("[") and s.endswith("]") and len(s) > 1:
            section = s[1:-1]
            if section not in sections_order:
                sections_order.append(section)
            desc, type_, default, acceptable, value_range = [], None, None, None, None
            continue
        if s.startswith("##"):
            desc.append(s[2:].strip())
            continue
        if s.startswith("# Setting type:"):
            type_ = s.split(":", 1)[1].strip()
            continue
        if s.startswith("# Default value:"):
            default = s.split(":", 1)[1].strip()
            continue
        if s.startswith("# Acceptable value range:"):
            nums = re.findall(r"-?\d+\.?\d*", s.split(":", 1)[1])
            if len(nums) >= 2:
                value_range = (nums[0], nums[1])
            continue
        if s.startswith("# Acceptable values:"):
            acceptable = [a.strip() for a in s.split(":", 1)[1].split(",") if a.strip()]
            continue
        if s.startswith("#"):
            continue
        if not s:
            continue
        if "=" in line and section is not None:
            key, val = line.split("=", 1)
            key = key.strip()
            val = val.strip()
            entries.append(Entry(section, key, val, default, type_, acceptable,
                                  value_range, " ".join(desc), i))
            desc, type_, default, acceptable, value_range = [], None, None, None, None

    return lines, entries, sections_order


# --------------------------------------------------------------- resolvers --

def resolve_mod(name):
    n = name.strip().lower()
    for key, info in MODS.items():
        if n == key or n in info["names"]:
            return key, info
    raise CfgError("unknown mod %r -- use one of: %s" %
                    (name, ", ".join(sorted(MODS.keys()))))


def resolve_server(name):
    n = (name or "test").strip().lower()
    if n not in SERVERS:
        raise CfgError("unknown server %r -- use test or live" % name)
    return n


def cfg_path(mod_info, server):
    return os.path.join(SERVERS[server]["root"], "config", "bepinex", mod_info["file"])


def find_entry(entries, target):
    if "." in target:
        section, key = target.split(".", 1)
        matches = [e for e in entries
                   if e.section.lower() == section.lower() and e.key.lower() == key.lower()]
        if not matches:
            raise CfgError("no such setting %s.%s" % (section, key))
        return matches[0]
    matches = [e for e in entries if e.key.lower() == target.lower()]
    if not matches:
        raise CfgError("no key named %r" % target)
    if len(matches) > 1:
        raise CfgError("key %r is ambiguous -- found in sections: %s (use Section.Key)" %
                        (target, ", ".join(e.section for e in matches)))
    return matches[0]


# -------------------------------------------------------------- validation --

def validate_value(entry, raw):
    if entry.acceptable:
        for c in entry.acceptable:
            if raw == c:
                return raw
        for c in entry.acceptable:
            if raw.lower() == c.lower():
                return c
        raise ValidationError("%r is not one of: %s" % (raw, ", ".join(entry.acceptable)))

    t = (entry.type or "").strip()
    if t == "Boolean":
        if raw.lower() not in ("true", "false"):
            raise ValidationError("%r is not a Boolean (true/false)" % raw)
        return raw.lower()

    if t in ("Int32", "Int64", "UInt32", "Int16", "Byte", "SByte"):
        try:
            iv = int(raw)
        except ValueError:
            raise ValidationError("%r is not an integer (%s)" % (raw, t))
        if entry.value_range:
            lo, hi = float(entry.value_range[0]), float(entry.value_range[1])
            if not (lo <= iv <= hi):
                raise ValidationError("%s out of range %s..%s" % (iv, lo, hi))
        return str(iv)

    if t in ("Single", "Double", "Decimal"):
        try:
            fv = float(raw)
        except ValueError:
            raise ValidationError("%r is not a number (%s)" % (raw, t))
        if entry.value_range:
            lo, hi = float(entry.value_range[0]), float(entry.value_range[1])
            if not (lo <= fv <= hi):
                raise ValidationError("%s out of range %s..%s" % (fv, lo, hi))
        return raw

    # String or an unrecognised/custom type with no acceptable-values list:
    # accept as-is (BepInEx itself would reject it on next reload if wrong).
    return raw


# -------------------------------------------------------------------- I/O --

def atomic_write(path, lines):
    d = os.path.dirname(path)
    fd, tmp = tempfile.mkstemp(dir=d, prefix=".cfgpy-tmp-")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines))
            f.write("\n")
        shutil.copymode(path, tmp)
        os.replace(tmp, path)
    except Exception:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def make_backup(path):
    ts = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = "%s.bak-%s" % (path, ts)
    n = 2
    while os.path.exists(backup):
        backup = "%s.bak-%s-%d" % (path, ts, n)
        n += 1
    shutil.copy2(path, backup)
    prune_backups(path)
    return backup


def prune_backups(path, keep=BACKUP_KEEP):
    backups = sorted(glob.glob(path + ".bak-*"))
    excess = len(backups) - keep
    for old in backups[:max(0, excess)]:
        try:
            os.unlink(old)
        except OSError:
            pass


def list_backups(path):
    return sorted(glob.glob(path + ".bak-*"))


# --------------------------------------------------------------- classify --

def classify(entry):
    if (None, entry.key) in RESTART_KEYS or (entry.section, entry.key) in RESTART_KEYS:
        return "restart", RESTART_KEYS.get((entry.section, entry.key)) or RESTART_KEYS[(None, entry.key)]
    if (entry.section, entry.key) in NEW_OBJECT_KEYS:
        return "new_objects", NEW_OBJECT_KEYS[(entry.section, entry.key)]
    return "live", None


# ---------------------------------------------------------------- docker ---

def docker_logs_since(container, since_epoch):
    try:
        out = subprocess.run(
            ["docker", "logs", "--since", str(int(since_epoch)), container],
            capture_output=True, text=True, errors="replace", timeout=10)
        return (out.stdout or "") + (out.stderr or "")
    except Exception as e:
        return ""


def wait_for_reload(container, since_epoch, section, key, timeout=5.0):
    needle = "[%s] %s " % (section, key)
    deadline = time.time() + timeout
    while True:
        text = docker_logs_since(container, since_epoch - 1)
        for line in text.splitlines():
            if "[Config] reloaded:" in line and needle in line and " -> " in line:
                return line.strip()
        if time.time() >= deadline:
            return None
        time.sleep(0.5)


def get_players(server):
    root = SERVERS[server]["root"]
    container = SERVERS[server]["container"]
    stats_dir = os.path.join(root, "config", "bepinex", "smoothserver", "stats")
    files = sorted(glob.glob(os.path.join(stats_dir, "stats-*.jsonl")), key=os.path.getmtime)
    if files:
        try:
            with open(files[-1], "rb") as f:
                f.seek(0, os.SEEK_END)
                size = f.tell()
                chunk = f.read(min(size, 8192)) if size else b""
                if size > 8192:
                    f.seek(-8192, os.SEEK_END)
                    chunk = f.read(8192)
                text = chunk.decode("utf-8", "replace")
                nonempty = [l for l in text.splitlines() if l.strip()]
                if nonempty:
                    rec = json.loads(nonempty[-1])
                    return {
                        "count": rec.get("players", {}).get("count"),
                        "names": rec.get("players", {}).get("names", []),
                        "source": "statslog",
                        "ts": rec.get("ts"),
                    }
        except Exception:
            pass
    # fallback: Connections line in the container log
    text = docker_logs_since(container, time.time() - 12 * 60)
    conn_lines = [l for l in text.splitlines() if re.search(r"Connections \d+", l)]
    if conn_lines:
        m = re.search(r"Connections (\d+)", conn_lines[-1])
        return {"count": int(m.group(1)) if m else None, "names": [], "source": "connections-log"}
    return {"count": None, "names": [], "source": "unknown"}


def get_status(server):
    container = SERVERS[server]["container"]
    text = docker_logs_since(container, time.time() - 2 * 60 * 60)
    lines = text.splitlines()
    versions = {}
    for mod_key, info in MODS.items():
        tag = info["display"]
        cand = [l for l in lines if ("[" + tag + "]" in l or ": " + tag + " " in l) and "CurrentVersion=" in l]
        if not cand:
            cand = [l for l in lines if "CurrentVersion=" in l and tag in l]
        if cand:
            m = re.search(r"CurrentVersion=([^\s]+)", cand[-1])
            versions[mod_key] = m.group(1) if m else "?"
        else:
            versions[mod_key] = None
    reload_lines = [l for l in lines if "[Config] reloaded:" in l]
    last_reload = reload_lines[-1].strip() if reload_lines else None
    return {
        "versions": versions,
        "players": get_players(server),
        "last_reload": last_reload,
    }


# ---------------------------------------------------------------- commands --

def cmd_list(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    lines, entries, sections = parse_cfg(path)
    if args.section:
        want = args.section.lower()
        matched = [e for e in entries if e.section.lower() == want]
        if not matched:
            print("no such section %r in %s" % (args.section, info["display"]), file=sys.stderr)
            return 1
        print("[%s]" % matched[0].section)
        for e in matched:
            print("  %s = %s  (%s)%s" % (e.key, e.value, e.meta(),
                                          "  -- " + e.description if e.description else ""))
    else:
        for s in sections:
            count = sum(1 for e in entries if e.section == s)
            print("%-16s %d keys" % (s, count))
    return 0


def cmd_get(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    lines, entries, sections = parse_cfg(path)
    if args.key2:
        target = args.key1 + "." + args.key2
    else:
        target = args.key1
    try:
        e = find_entry(entries, target)
    except CfgError as ex:
        print("error: %s" % ex, file=sys.stderr)
        return 1
    print("%s = %s  (%s)" % (e.label(), e.value, e.meta()))
    if e.description:
        print("  " + e.description)
    return 0


def cmd_set(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    lines, entries, sections = parse_cfg(path)
    try:
        e = find_entry(entries, args.target)
    except CfgError as ex:
        print("error: %s" % ex, file=sys.stderr)
        return 1

    old_value = e.value
    if args.force:
        new_value = args.value
    else:
        try:
            new_value = validate_value(e, args.value)
        except ValidationError as ex:
            print("REJECTED: %s -- %s" % (e.label(), ex), file=sys.stderr)
            return 2

    kind, note = classify(e)

    if args.dry_run:
        print("[dry-run] would set %s: %s -> %s  (%s)" % (e.label(), old_value, new_value, path))
        if note:
            print("  note: %s" % note)
        return 0

    if server == "live":
        print("== target: LIVE server (%s, container %s) ==" % (path, SERVERS[server]["container"]),
              file=sys.stderr)

    backup = make_backup(path)
    since = time.time()
    new_lines = list(lines)
    new_lines[e.line_index] = "%s = %s" % (e.key, new_value)
    atomic_write(path, new_lines)

    print("backed up -> %s" % backup)
    print("wrote %s = %s  (was %s)" % (e.label(), new_value, old_value))

    if kind == "restart":
        print("written (needs restart: %s)" % e.key)
        if note:
            print("  note: %s" % note)
        return 0

    if args.no_wait:
        print("written (--no-wait, not confirming reload)")
        return 0

    container = SERVERS[server]["container"]
    line = wait_for_reload(container, since, e.section, e.key, timeout=5.0)
    if line:
        print("  log: %s" % line)
        if kind == "new_objects":
            print("written (applies to new objects)")
            if note:
                print("  note: %s" % note)
        else:
            print("applied live")
    else:
        print("written (no reload confirmed within 5s -- check `docker logs %s`, "
              "or no world is loaded yet)" % container)
    return 0


def cmd_diff(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    lines, entries, sections = parse_cfg(path)
    changed = [e for e in entries if e.default is not None and e.value != e.default]
    if not changed:
        print("no settings differ from default in %s (%s)" % (info["display"], server))
        return 0
    for e in changed:
        print("%s = %s  (default %s)" % (e.label(), e.value, e.default))
    return 0


def cmd_restore(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    backups = list_backups(path)
    if not backups:
        print("no backups found for %s" % path, file=sys.stderr)
        return 1
    if args.backup:
        chosen = args.backup
        if not os.path.isabs(chosen):
            chosen = os.path.join(os.path.dirname(path), os.path.basename(chosen))
        if chosen not in backups:
            print("no such backup %r" % args.backup, file=sys.stderr)
            print("available:\n  " + "\n  ".join(backups), file=sys.stderr)
            return 1
    else:
        chosen = backups[-1]

    since = time.time()
    shutil.copy2(chosen, path)
    print("restored %s <- %s" % (path, chosen))

    if server == "live":
        print("== target: LIVE server ==", file=sys.stderr)

    container = SERVERS[server]["container"]
    deadline = time.time() + 3.0
    seen = None
    while time.time() < deadline:
        text = docker_logs_since(container, since - 1)
        cand = [l for l in text.splitlines() if "[Config] reloaded:" in l]
        if cand:
            seen = cand[-1].strip()
            break
        time.sleep(0.5)
    if seen:
        print("  log: %s" % seen)
    else:
        print("  (no reload line seen within 3s -- check `docker logs %s`)" % container)
    return 0


def cmd_players(args):
    server = resolve_server(args.server)
    p = get_players(server)
    names = ", ".join(p["names"]) if p["names"] else "(none)"
    ts = (" as of " + p["ts"]) if p.get("ts") else ""
    print("%s: %s players -- %s  [source: %s%s]" %
          (server, p["count"], names, p["source"], ts))
    return 0


def cmd_status(args):
    server = resolve_server(args.server)
    st = get_status(server)
    print("server: %s" % server)
    for mod_key, info in MODS.items():
        v = st["versions"].get(mod_key)
        print("  %-18s %s" % (info["display"], v if v else "(no version line seen in last 2h of logs)"))
    p = st["players"]
    names = ", ".join(p["names"]) if p["names"] else "(none)"
    print("  players: %s -- %s  [source: %s]" % (p["count"], names, p["source"]))
    print("  last reload: %s" % (st["last_reload"] or "(none seen in last 2h of logs)"))
    return 0


def cmd_search(args):
    mod_key, info = resolve_mod(args.mod)
    server = resolve_server(args.server)
    path = cfg_path(info, server)
    lines, entries, sections = parse_cfg(path)
    text = args.text.lower()
    hits = [e for e in entries
            if text in e.key.lower() or text in (e.description or "").lower()
            or text in e.section.lower()]
    if not hits:
        print("no matches for %r in %s" % (args.text, info["display"]))
        return 0
    for e in hits:
        print("%s = %s  (%s)" % (e.label(), e.value, e.meta()))
        if e.description:
            print("  " + e.description)
    return 0


# ------------------------------------------------------------------- main --

def build_parser():
    p = argparse.ArgumentParser(prog="cfg.py",
                                 description="Read/write NVLB + SmoothServer BepInEx config live.")
    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("list", help="list sections, or keys in a section")
    sp.add_argument("mod")
    sp.add_argument("section", nargs="?")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_list)

    sp = sub.add_parser("get", help="get one setting (Section Key, or just Key if unique)")
    sp.add_argument("mod")
    sp.add_argument("key1")
    sp.add_argument("key2", nargs="?")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_get)

    sp = sub.add_parser("set", help="set one setting: Section.Key value (or Key value if unique)")
    sp.add_argument("mod")
    sp.add_argument("target")
    sp.add_argument("value")
    sp.add_argument("--server", default="test", choices=["test", "live"])
    sp.add_argument("--no-wait", action="store_true")
    sp.add_argument("--force", action="store_true", help="skip type/range validation")
    sp.add_argument("--dry-run", action="store_true")
    sp.set_defaults(func=cmd_set)

    sp = sub.add_parser("diff", help="every key whose value != default")
    sp.add_argument("mod")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_diff)

    sp = sub.add_parser("restore", help="restore a backup (latest by default)")
    sp.add_argument("mod")
    sp.add_argument("backup", nargs="?")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_restore)

    sp = sub.add_parser("players", help="current player count + names")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_players)

    sp = sub.add_parser("status", help="both mods' versions, players, last reload")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_status)

    sp = sub.add_parser("search", help="grep keys + descriptions")
    sp.add_argument("mod")
    sp.add_argument("text")
    sp.add_argument("--server", default="test")
    sp.set_defaults(func=cmd_search)

    return p


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args) or 0
    except CfgError as e:
        print("error: %s" % e, file=sys.stderr)
        return 1
    except FileNotFoundError as e:
        print("error: %s" % e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
