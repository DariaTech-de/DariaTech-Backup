#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 1 ]]; then echo 'Usage: generate-update-signing-key.sh OFFLINE_PRIVATE_DIRECTORY' >&2; exit 2; fi
umask 077
mkdir -p -- "$1"
if [[ -e "$1/updates-private.pem" || -e "$1/updates-public.pem" ]]; then echo 'Refusing to overwrite update trust keys' >&2; exit 1; fi
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$1/updates-private.pem"
openssl pkey -in "$1/updates-private.pem" -pubout -out "$1/updates-public.pem"
echo 'Keep updates-private.pem offline, separate from the Console command key. Pin updates-public.pem on Console and agents through your trusted deployment channel.'
