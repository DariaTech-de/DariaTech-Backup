#!/usr/bin/env python3
"""Collect tested CI artifacts into one agent release with stable file names and a manifest.

Usage: assemble-agent-release.py DOWNLOAD_DIR OUTPUT_DIR VERSION OWNER/REPO
The Console reads agent-release.json to offer the matching package per platform.
"""
import hashlib, json, pathlib, sys, datetime

source, output, version, repository = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3], sys.argv[4]
output.mkdir(parents=True, exist_ok=True)

def verified(path: pathlib.Path) -> str:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    recorded = (path.parent / (path.name + ".sha256")).read_text().split()[0].lower()
    if digest != recorded:
        raise SystemExit(f"Checksum mismatch for {path.name}")
    return digest

platforms = [
    ("win-x64", "Windows", "DariaTechBackupSetup-win-x64.exe", source / "DariaTechBackupSetup-win-x64-unsigned" / "DariaTechBackupSetup.exe"),
    *[(rid, label, f"DariaTechBackupAgent-{rid}.tar.gz", source / f"DariaTechBackupAgent-{rid}-package" / f"DariaTechBackupAgent-{version}-{rid}.tar.gz")
      for rid, label in [("linux-x64", "Linux x64"), ("linux-arm64", "Linux ARM64"), ("osx-arm64", "macOS Apple Silicon"), ("osx-x64", "macOS Intel")]],
]
windows_version = (source / "DariaTechBackupSetup-win-x64-unsigned" / "agent-version.txt").read_text().strip()
if windows_version != version + ".0":
    raise SystemExit(f"Windows installer version {windows_version} does not match {version}")

assets, sums = [], []
for rid, label, name, built in platforms:
    if not built.is_file():
        raise SystemExit(f"Missing tested artifact for {rid}: {built}")
    digest = verified(built)
    data = built.read_bytes()
    (output / name).write_bytes(data)
    sums.append(f"{digest}  {name}")
    assets.append({"platform": rid, "label": label, "file": name, "sha256": digest, "size": len(data),
                   "url": f"https://github.com/{repository}/releases/download/agent-v{version}/{name}"})

(output / "SHA256SUMS").write_text("\n".join(sums) + "\n")
(output / "agent-release.json").write_text(json.dumps({
    "version": version, "tag": f"agent-v{version}",
    "created": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    "assets": assets}, indent=2) + "\n")
output.with_name("RELEASE_NOTES.md").write_text(f"""DariaTech Backup Agent {version} for Windows, Linux and macOS.

Every package was built and tested in this run: Windows service installation, enrollment, real engine,
upgrade and signed update (windows-2025); native service, HTTPS enrollment, real encrypted backup/restore
and signed update on Linux x64/ARM64 and macOS Intel/Apple Silicon.

Install from the Console: Kunde → Gerät hinzufügen → Plattform wählen. The Console shows the download and a
ready-to-run command with the one-time registration token. Checksums: SHA256SUMS.

The Windows EXE and macOS binaries are not yet code-signed/notarized; Windows SmartScreen may warn.
""")
print(f"{len(assets)} packages assembled for agent-v{version}")
