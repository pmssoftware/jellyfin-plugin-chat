#!/usr/bin/env python3
"""Add Jellyfin Chat release artifacts to the plugin repository manifest."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import re
import sys


ARTIFACT_PATTERN = re.compile(r"^chat_(?P<server>10\.11|12\.0)_(?P<version>\d+\.\d+\.\d+\.\d+)\.zip$")
TARGET_ABIS = {"10.11": "10.11.11.0", "12.0": "12.0.0.0"}


def checksum(path: pathlib.Path) -> str:
    digest = hashlib.md5(usedforsecurity=False)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--artifacts", type=pathlib.Path, required=True)
    args = parser.parse_args()

    version = args.tag.removeprefix("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        print("Release tags must look like v1.2.3.4", file=sys.stderr)
        return 2

    manifest_path = pathlib.Path("manifest.json")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, list) or len(manifest) != 1:
        raise ValueError("manifest.json must contain exactly one plugin entry")

    plugin = manifest[0]
    timestamp = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    additions = []
    for archive in sorted(args.artifacts.glob("chat_*.zip")):
        match = ARTIFACT_PATTERN.match(archive.name)
        if not match or match.group("version") != version:
            continue
        server = match.group("server")
        additions.append({
            "version": version,
            "changelog": "See the linked GitHub release for changes.",
            "targetAbi": TARGET_ABIS[server],
            "sourceUrl": "https://github.com/{}/releases/download/{}/{}".format(args.repository, args.tag, archive.name),
            "checksum": checksum(archive),
            "timestamp": timestamp,
        })

    if len(additions) != len(TARGET_ABIS):
        raise FileNotFoundError("Expected one release artifact for each supported Jellyfin server line")
    plugin["versions"] = additions
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
