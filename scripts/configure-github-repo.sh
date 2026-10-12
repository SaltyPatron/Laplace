#!/usr/bin/env bash
# Compatibility entry point: machine configuration is installation-scoped.
set -euo pipefail
printf '%s\n' 'Host paths, database roles, browser origins and authentication settings belong in each installation.' 'This helper no longer copies one machine configuration into global GitHub repository variables.'
