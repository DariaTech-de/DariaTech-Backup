#!/usr/bin/env python3
"""Build self-contained OSS agent packages; never reuse a full/proprietary publish."""
import argparse, hashlib, json, os, re, shutil, subprocess, tarfile
from pathlib import Path
from xml.etree import ElementTree

repo = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument("--rid", required=True, choices=["linux-x64","linux-arm64","osx-x64","osx-arm64"])
parser.add_argument("--version")
args = parser.parse_args()
version = args.version or ElementTree.parse(repo/"DariaTech/Agent/DariaTech.Agent.csproj").findtext("./PropertyGroup/Version")
if not re.fullmatch(r"\d+\.\d+\.\d+", version or ""):
    raise SystemExit("Expected a three-component package version")
root = repo/"artifacts/unix"/args.rid
if root.is_symlink():
    raise SystemExit("Generated package directory cannot be a link")
if root.exists():
    shutil.rmtree(root)
payload = root/"payload"
agent = payload/"agent"
engine = agent/"engine"
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DO_NOT_TRACK="1", AUTOUPDATER_Duplicati_SKIP_UPDATE="1")
def run(command):
    subprocess.run(command, cwd=repo, env=env, check=True)
# RID publishes use separate restore graphs. Preserve the checked-in portable lock
# files so a package build cannot mutate source or break the locked Console build.
lock_paths = [repo/"DariaTech/Agent/packages.lock.json",repo/"DariaTech/Contracts/packages.lock.json"]
portable_locks = {p:p.read_bytes() for p in lock_paths}
try:
    run(["dotnet","publish","DariaTech/Agent/DariaTech.Agent.csproj","-c","Release","-r",args.rid,"--self-contained","true","-p:Version="+version,"-o",str(agent)])
    run(["dotnet","publish","Executables/Duplicati.Server/Duplicati.Server.csproj","-c","Release","-r",args.rid,"--self-contained","true","-p:DariaTechOssOnly=true","-o",str(engine)])
finally:
    for path,content in portable_locks.items():
        path.write_bytes(content)
# Devices are managed from the Console only: the packaged engine serves a notice instead of the Duplicati web
# interface (with its remote-control and update settings). Only packaged files change, not engine code.
shutil.rmtree(engine/"webroot")
shutil.copytree(repo/"DariaTech/Agent/local-ui", engine/"webroot")
for file in engine.rglob("*"):
    if file.is_symlink():
        raise SystemExit("Links are not allowed in release payloads")
    if file.is_file() and re.search(r"(Proprietary|Office365|GoogleWorkspace|DiskImage).*\.(dll|exe)$",file.name,re.I):
        raise SystemExit("Restricted provider assembly in OSS package")
legal = agent/"legal"
legal.mkdir()
shutil.copy2(repo/"LICENSE", legal/"Duplicati-LICENSE")
shutil.copytree(repo/"Duplicati/License", legal/"duplicati")
cache = subprocess.check_output(["dotnet","nuget","locals","global-packages","--list"],cwd=repo,text=True).strip().split(": ",1)[1]
for package in ["microsoft.netcore.app.runtime."+args.rid,"microsoft.aspnetcore.app.runtime."+args.rid]:
    versions = list((Path(cache)/package).iterdir())
    if not versions:
        raise SystemExit("Published runtime legal notices unavailable")
    selected = max(versions,key=lambda p: tuple(int(x) for x in p.name.split(".")[:3]))
    destination = legal/package
    destination.mkdir()
    notices = [p for p in selected.iterdir() if p.name.upper() in ["LICENSE.TXT","THIRD-PARTY-NOTICES.TXT"]]
    if not notices:
        raise SystemExit("Runtime license notices missing")
    for notice in notices:
        shutil.copy2(notice,destination/notice.name)
brand = json.loads((repo/"branding/product.json").read_text())
service = brand["windowsServiceName"]
if not re.fullmatch(r"[A-Za-z][A-Za-z0-9]{1,60}",service):
    raise SystemExit("Unsafe centrally configured service name")
(agent/"service.json").write_text(json.dumps({"linuxService":service,"macServiceLabel":"de.dariatech."+service.lower()},indent=2)+"\n")
shutil.copy2(repo/"deploy/unix/install.sh",payload/"install.sh")
shutil.copy2(repo/"deploy/unix/uninstall.sh",payload/"uninstall.sh")
(payload/"platform.txt").write_text(args.rid+"\n")
(payload/"version.txt").write_text(version+".0\n")
for path in payload.rglob("*"):
    if path.is_symlink():
        raise SystemExit("Release payload cannot contain links")
    path.chmod(0o755 if path.is_dir() or path.name in ["DariaTech.Agent","Duplicati.Server","install.sh","uninstall.sh"] else 0o644)
archive = root/("DariaTechBackupAgent-"+version+"-"+args.rid+".tar.gz")
with tarfile.open(archive,"w:gz",format=tarfile.PAX_FORMAT) as output:
    for path in sorted(payload.rglob("*")):
        info = output.gettarinfo(str(path),str(path.relative_to(payload)))
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        info.mtime = int(os.environ.get("SOURCE_DATE_EPOCH","0"))
        if path.is_file():
            with path.open("rb") as stream:
                output.addfile(info,stream)
        else:
            output.addfile(info)
(archive.with_suffix(archive.suffix+".sha256")).write_text(hashlib.sha256(archive.read_bytes()).hexdigest()+"  "+archive.name+"\n")
print(archive.relative_to(repo))
