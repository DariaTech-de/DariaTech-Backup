#!/usr/bin/env python3
"""Generate browser assets from the sole product manifest; --check verifies drift."""
import json, pathlib, sys
root = pathlib.Path(__file__).resolve().parents[1]
source = root / "branding"
b = json.loads((source / "product.json").read_text())
outputs = {}
for folder in [root / "Duplicati/Server/webroot/branding", root / "DariaTech/Console/wwwroot/branding"]:
    outputs[folder / "product.json"] = (source / "product.json").read_text()
    for name in ["logo.svg", "favicon.svg"]:
        outputs[folder / name] = (source / name).read_text()
    outputs[folder / "product.js"] = "window.DariaTechBranding = " + json.dumps(b) + ";\n"
    outputs[folder / "product.css"] = ":root { --brand-primary: " + b["primaryColor"] + "; }\n"
for path, content in outputs.items():
    if "--check" in sys.argv:
        if not path.exists() or path.read_text() != content:
            raise SystemExit("Branding drift: " + str(path.relative_to(root)))
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
