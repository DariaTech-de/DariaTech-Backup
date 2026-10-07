#!/usr/bin/env python3
"""Create deployment secrets locally; refuses to overwrite an existing secret directory."""
import base64, pathlib, secrets, os
root=pathlib.Path(__file__).resolve().parents[1]/".secrets"
root.mkdir(mode=0o700)
owner=secrets.token_urlsafe(48)
application=secrets.token_urlsafe(48)
values={"postgres-password":owner,"app-password":application,"master.key":base64.b64encode(secrets.token_bytes(32)).decode(),
 "app-connection":f"Host=postgres;Database=dariatech;Username=dariatech_app;Password={application};Include Error Detail=false;GSS Encryption Mode=Disable",
 "migration-connection":f"Host=postgres;Database=dariatech;Username=postgres;Password={owner};Include Error Detail=false;GSS Encryption Mode=Disable"}
for name,value in values.items():
    p=root/name
    with p.open("x") as f: f.write(value+"\n")
    # Single-file Docker secret mounts need to be readable by the container service UID.
    # The containing host directory remains 0700; never expose it as a whole-directory mount.
    p.chmod(0o444)
print("Deployment secrets created in protected .secrets/; back up the master key securely.")
