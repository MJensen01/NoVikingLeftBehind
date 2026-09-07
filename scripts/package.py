#!/usr/bin/env python3
"""Build a Thunderstore/r2modman-style zip for NoVikingLeftBehind from an already-built DLL.

Adapted from the box lab's `/opt/modlab/package.py` (which generated its own placeholder PNG
icons at build time). This version reads the real icon/README/CHANGELOG/manifest that live in
`thunderstore/` and only fills in the DLL, so editing those files is the way to change the
mod's Thunderstore listing content — this script does not hardcode any of it.

Usage (after `dotnet build -c Release`):
    python scripts/package.py [--config Release]

Output: dist/NoVikingLeftBehind-<version>.zip
"""
import argparse
import json
import os
import shutil
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(REPO_ROOT, "dist")

MOD = {
    "name": "NoVikingLeftBehind",
    "src_dir": os.path.join(REPO_ROOT, "src"),
    "ts_dir": os.path.join(REPO_ROOT, "thunderstore"),
}


def build_one(mod, configuration):
    name = mod["name"]
    dll_path = os.path.join(mod["src_dir"], "bin", f"{name}.dll")
    if not os.path.exists(dll_path):
        raise SystemExit(
            f"error: {dll_path} not found — run `dotnet build -c {configuration} src` first"
        )

    manifest_path = os.path.join(mod["ts_dir"], "manifest.json")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    version = manifest["version_number"]

    stage = os.path.join(DIST, f"stage-{name}")
    shutil.rmtree(stage, ignore_errors=True)
    os.makedirs(stage)

    shutil.copy(dll_path, os.path.join(stage, f"{name}.dll"))
    pdb_path = os.path.join(mod["src_dir"], "bin", f"{name}.pdb")
    if os.path.exists(pdb_path):
        # DebugType is `none` in Directory.Build.props, so this normally won't exist; copy it
        # anyway if a local override produced one, for easier debugging of a local install.
        shutil.copy(pdb_path, os.path.join(stage, f"{name}.pdb"))

    for fname in ("manifest.json", "README.md", "CHANGELOG.md", "icon.png"):
        src = os.path.join(mod["ts_dir"], fname)
        if not os.path.exists(src):
            raise SystemExit(f"error: {src} missing — required for a Thunderstore package")
        shutil.copy(src, os.path.join(stage, fname))

    os.makedirs(DIST, exist_ok=True)
    out_path = os.path.join(DIST, f"{name}-{version}.zip")
    if os.path.exists(out_path):
        os.remove(out_path)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        for fname in sorted(os.listdir(stage)):
            z.write(os.path.join(stage, fname), fname)
    shutil.rmtree(stage)
    print(f"{out_path}  ({os.path.getsize(out_path)} bytes)")
    return out_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="Release", help="Build configuration the DLL was built with (informational only; the DLL is always read from bin/)")
    args = parser.parse_args()

    os.makedirs(DIST, exist_ok=True)
    out = build_one(MOD, args.config)
    print(f"\nBuilt 1 package into {DIST}")


if __name__ == "__main__":
    main()
