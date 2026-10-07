#!/usr/bin/env python3
"""Generate browser assets from the sole product manifest; --check verifies drift."""
import json, pathlib, sys
root = pathlib.Path(__file__).resolve().parents[1]
source = root / "branding"
b = json.loads((source / "product.json").read_text())
outputs = {}
for folder in [root / "Duplicati/Server/webroot/branding", root / "DariaTech/Console/wwwroot/branding"]:
    outputs[folder / "product.json"] = (source / "product.json").read_text()
    for key in ["logo", "favicon", "companyLogo"]:
        name = pathlib.PurePosixPath(b[key]).name
        outputs[folder / name] = (source / name).read_bytes()
    outputs[folder / "product.js"] = "window.DariaTechBranding = " + json.dumps(b) + ";\n"
    colors = {"primary": b["primaryColor"], **b["palette"]}
    import re
    if any(not re.fullmatch(r"#[0-9a-fA-F]{6}", value) for value in colors.values()):
        raise SystemExit("Branding colors must be six-digit hex values")
    outputs[folder / "product.css"] = ":root { " + " ".join(
        "--brand-" + re.sub(r"([A-Z])", r"-\1", key).lower() + ": " + value + ";"
        for key, value in colors.items()) + " }\n"
outputs[root / "Duplicati/Server/webroot/branding/engine.css"] = (source / "engine.css").read_text()
engine_css = root / "Duplicati/Server/webroot/branding/product.css"
outputs[engine_css] += "a { color: var(--brand-primary); } .btn-primary, .progress-bar { background-color: var(--brand-primary); border-color: var(--brand-primary); } .btn-primary:hover, .btn-primary:focus { background-color: var(--brand-sidebar); border-color: var(--brand-sidebar); } .navbar-inverse { background-color: var(--brand-sidebar); }\n"
for path, content in outputs.items():
    content = content if isinstance(content, bytes) else content.encode("utf-8")
    if "--check" in sys.argv:
        if not path.exists() or path.read_bytes() != content:
            raise SystemExit("Branding drift: " + str(path.relative_to(root)))
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
