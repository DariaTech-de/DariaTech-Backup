#!/usr/bin/env python3
"""Inventory restored Console/Agent packages and retain NuGet/SPDX license notices."""
import json, pathlib, zipfile, xml.etree.ElementTree as ET, urllib.request
root=pathlib.Path(__file__).resolve().parents[1]
output=root/"DariaTech/Console/wwwroot/legal/management"
output.mkdir(parents=True, exist_ok=True)
items={}
for project in ["Console", "Agent"]:
    assets=json.loads((root/f"DariaTech/{project}/obj/project.assets.json").read_text())
    cache=pathlib.Path(next(iter(assets["packageFolders"])))
    for name, library in assets["libraries"].items():
        if library["type"]!="package": continue
        with zipfile.ZipFile(next((cache/library["path"]).glob("*.nupkg"))) as archive:
            nuspec=next(x for x in archive.namelist() if x.endswith(".nuspec"))
            nodes={x.tag.split("}")[-1]:x for x in ET.fromstring(archive.read(nuspec)).iter()}
            license=nodes.get("license")
            if license is None: raise SystemExit("Manual license review required: "+name)
            kind=license.attrib["type"]; value=license.text
            if kind=="file":
                filename=name.replace("/","-")+"-LICENSE.txt"
                (output/filename).write_bytes(archive.read(value))
            else:
                if value not in {"MIT", "PostgreSQL"}: raise SystemExit("Manual SPDX review required: "+str(value))
                filename=value+".txt"
                if not (output/filename).exists():
                    url="https://raw.githubusercontent.com/spdx/license-list-data/main/text/"+filename
                    (output/filename).write_bytes(urllib.request.urlopen(url, timeout=30).read())
            items[name]={"package":name,"license":value,"text":filename,
                "authors":nodes["authors"].text if "authors" in nodes else "",
                "copyright":nodes["copyright"].text if "copyright" in nodes else ""}
(output/"inventory.json").write_text(json.dumps(list(items.values()),indent=2)+"\n")
print(f"Retained license metadata for {len(items)} packages, including build-time dependencies.")
