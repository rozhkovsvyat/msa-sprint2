#!/usr/bin/env bash
# Сравниваем реально запущенный артефакт, а не только текстовый тег Deployment.
set -euo pipefail
RELEASE="${RELEASE:-booking-service}"
STAGING_NAMESPACE="${STAGING_NAMESPACE:-staging}"
PROD_NAMESPACE="${PROD_NAMESPACE:-prod}"
image_ids() {
  kubectl -n "$1" get pods -l "app=$RELEASE" -o json \
    | jq -r '.items[] | select(.metadata.deletionTimestamp == null and .status.phase == "Running") |
      .status.containerStatuses[]? | select(.name == "booking-service" and .ready == true) | .imageID' | sort -u
}
stage=$(image_ids "$STAGING_NAMESPACE")
prod=$(image_ids "$PROD_NAMESPACE")
echo "staging ($STAGING_NAMESPACE): $stage"
echo "prod ($PROD_NAMESPACE): $prod"
[[ -n "$stage" && "$stage" != *$'\n'* && "$stage" == "$prod" ]] \
  || { echo '[FAIL] Среды не используют один проверенный артефакт' >&2; exit 1; }
echo '[PASS] В обеих средах один imageID; пересборки при продвижении нет'
