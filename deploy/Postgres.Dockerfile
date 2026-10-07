FROM postgres:17-bookworm
COPY --chmod=0755 deploy/postgres-init.sh /docker-entrypoint-initdb.d/10-app-user.sh
