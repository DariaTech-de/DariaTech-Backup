#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/repo"
export UPDATE_TEST_LOG="$fixture/calls" DARIATECH_BACKUP_DIR="$fixture/repo" DARIATECH_BACKUP_BACKUP_DIR="$fixture/backups" DARIATECH_BACKUP_LOCK_FILE="$fixture/lock"
cat > "$fixture/bin/git" <<'SH'
#!/usr/bin/env bash
[[ $1 == status ]] && exit 0
printf 'git %s\n' "$*" >> "$UPDATE_TEST_LOG"
[[ ${FAIL_STAGE:-} == fetch ]] && exit 42
exit 0
SH
cat > "$fixture/bin/docker" <<'SH'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$UPDATE_TEST_LOG"
case "$*" in
 'compose build console') [[ ${FAIL_STAGE:-} == build ]] && exit 42 ;;
 'compose exec -T postgres '*) [[ ${FAIL_STAGE:-} == backup ]] && exit 42; echo 'test database dump' ;;
 'compose --profile maintenance run --rm migrate') [[ ${FAIL_STAGE:-} == migrate ]] && exit 42 ;;
 'compose up -d --wait --wait-timeout 120 console') [[ ${FAIL_STAGE:-} == health ]] && exit 42 ;;
esac
exit 0
SH
chmod +x "$fixture/bin/"*
export PATH="$fixture/bin:$PATH"
for FAIL_STAGE in fetch build backup migrate health; do
 export FAIL_STAGE
 : > "$UPDATE_TEST_LOG"
 if bash "$root/scripts/dariatech-backup-update" > "$fixture/output" 2>&1; then echo "Unexpected success: $FAIL_STAGE" >&2; exit 1; fi
 if [[ $FAIL_STAGE != health ]] && grep -q 'compose up ' "$UPDATE_TEST_LOG"; then echo 'Restart after failure' >&2; exit 1; fi
 if [[ $FAIL_STAGE == fetch ]] && grep -q 'compose build ' "$UPDATE_TEST_LOG"; then exit 1; fi
 if [[ $FAIL_STAGE == build || $FAIL_STAGE == backup ]] && grep -q ' run --rm migrate' "$UPDATE_TEST_LOG"; then exit 1; fi
 if grep -q 'erfolgreich aktualisiert' "$fixture/output"; then exit 1; fi
done
unset FAIL_STAGE
bash "$root/scripts/dariatech-backup-update" > "$fixture/output" 2>&1
grep -q 'erfolgreich aktualisiert' "$fixture/output"
grep -q 'compose --profile maintenance run --rm migrate' "$UPDATE_TEST_LOG"
[[ -n $(find "$fixture/backups" -name '*-database.sql' -print -quit) ]]
echo 'Updater: success and five failure paths passed.'
