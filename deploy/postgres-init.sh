#!/usr/bin/env bash
set -euo pipefail
# No superuser credentials are provided to the running web application.
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set=ON_ERROR_STOP=1 <<'SQL'
\set app_password `cat /run/secrets/app_password`
CREATE ROLE dariatech_app LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
GRANT CONNECT ON DATABASE dariatech TO dariatech_app;
GRANT USAGE ON SCHEMA public TO dariatech_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO dariatech_app;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public GRANT USAGE,SELECT ON SEQUENCES TO dariatech_app;
SQL
