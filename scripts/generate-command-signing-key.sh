#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 1 ]]; then echo 'Usage: generate-command-signing-key.sh PRIVATE_DIRECTORY' >&2; exit 2; fi
umask 077
mkdir -p -- "$1"
if [[ -e "$1/commands-private.pem" || -e "$1/commands-public.pem" ]]; then echo 'Refusing to overwrite signing keys' >&2; exit 1; fi
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$1/commands-private.pem"
openssl pkey -in "$1/commands-private.pem" -pubout -out "$1/commands-public.pem"
echo 'Created private Console key and public agent trust key. Keep private key outside Git; pin public key on agents through your trusted deployment channel.'
