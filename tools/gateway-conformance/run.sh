#!/usr/bin/env bash
# Runs the Gateway API conformance suite (Gateway + HTTPRoute core, plus the extended features listed below) against
# Titanium Gateway in a throwaway kind cluster.
#
# Needs: docker, kind, kubectl, helm. Run from anywhere; paths are relative to the repository root.
#   tools/gateway-conformance/run.sh
#
# The suite itself runs in a golang container on the kind network, so the host needs no Go toolchain and can reach the
# MetalLB addresses the Gateway gets (a Docker Desktop host cannot route to kind node addresses).
#
# Settings (environment):
#   CLUSTER          kind cluster name (default titanium-conformance)
#   KEEP_CLUSTER=1   leave the cluster running afterwards
#   SKIP_BUILD=1     reuse the images titanium-web-proxy:ci and titanium-gateway:ci
#   REPORT           report path (default artifacts/gateway-conformance-report.yaml)
#   RUN_TESTS        a -run-test name to run a single conformance test (default: the whole profile)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CLUSTER="${CLUSTER:-titanium-conformance}"
NAMESPACE="gateway-system"
RELEASE="titanium-gateway"
METALLB_VERSION="v0.14.9"
GOLANG_IMAGE="golang:1.23"
REPORT="${REPORT:-$ROOT/artifacts/gateway-conformance-report.yaml}"
VERSION="$(sed -n 's/^appVersion: "\(.*\)"/\1/p' "$ROOT/deploy/helm/titanium-gateway/Chart.yaml")"

# Extended features this controller claims (see website/docs/gateway-api.md). Everything not listed is not claimed.
# Not claimed, because the data plane cannot honor them: response header add (it keeps one value per header) and a
# redirect without a hostname (HTTPRouteSchemeRedirect, HTTPRoutePortRedirect, HTTPRoutePathRedirect).
SUPPORTED_FEATURES="HTTPRouteQueryParamMatching,HTTPRouteMethodMatching,HTTPRouteHostRewrite,HTTPRoutePathRewrite,HTTPRouteRequestMirror"

cleanup() {
  if [[ "${KEEP_CLUSTER:-0}" != "1" ]]; then
    kind delete cluster --name "$CLUSTER" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  docker build -f "$ROOT/packaging/docker/Dockerfile" -t titanium-web-proxy:ci \
    --build-arg VERSION=0.0.0-ci --build-arg COMMIT=local "$ROOT"
  docker build -f "$ROOT/packaging/docker/Dockerfile.gateway" -t titanium-gateway:ci \
    --build-arg VERSION=0.0.0-ci --build-arg COMMIT=local "$ROOT"
fi

if ! kind get clusters | grep -qx "$CLUSTER"; then
  kind create cluster --name "$CLUSTER" --wait 120s
fi
kind load docker-image titanium-web-proxy:ci titanium-gateway:ci --name "$CLUSTER"
kubectl config use-context "kind-$CLUSTER" >/dev/null

# LoadBalancer addresses: MetalLB hands out addresses from the top of the kind network, which the suite container reaches.
kubectl apply -f "https://raw.githubusercontent.com/metallb/metallb/${METALLB_VERSION}/config/manifests/metallb-native.yaml"
kubectl -n metallb-system rollout status deployment/controller --timeout=180s
kubectl -n metallb-system rollout status daemonset/speaker --timeout=180s
SUBNET="$(docker network inspect kind -f '{{range .IPAM.Config}}{{.Subnet}}{{"\n"}}{{end}}' | grep -E '^[0-9]+\.[0-9]+\.' | head -n 1)"
PREFIX="$(echo "$SUBNET" | cut -d. -f1,2)"
cat <<YAML | kubectl apply -f -
apiVersion: metallb.io/v1beta1
kind: IPAddressPool
metadata:
  name: conformance
  namespace: metallb-system
spec:
  addresses:
    - ${PREFIX}.255.200-${PREFIX}.255.250
---
apiVersion: metallb.io/v1beta1
kind: L2Advertisement
metadata:
  name: conformance
  namespace: metallb-system
YAML

helm upgrade --install "$RELEASE" "$ROOT/deploy/helm/titanium-gateway" \
  --namespace "$NAMESPACE" --create-namespace \
  -f "$ROOT/deploy/helm/titanium-gateway/ci/kind-values.yaml" \
  --wait --timeout 5m
kubectl wait gatewayclass/titanium --for=condition=Accepted --timeout=120s

mkdir -p "$(dirname "$REPORT")"
KUBECONFIG_FILE="$(mktemp)"
kind get kubeconfig --internal --name "$CLUSTER" > "$KUBECONFIG_FILE"
REPORT_DIR="$(cd "$(dirname "$REPORT")" && pwd)"

RUN_ARGS=()
if [[ -n "${RUN_TESTS:-}" ]]; then
  RUN_ARGS=(--run-test "$RUN_TESTS")
fi

# A failing conformance test must not stop the script before the report is copied out, so remember the exit code.
set +e
docker run --rm --network kind \
  -e KUBECONFIG=/kubeconfig \
  -v "$KUBECONFIG_FILE:/kubeconfig:ro" \
  -v "$ROOT/tests/gateway-conformance:/src" \
  -v "$REPORT_DIR:/report" \
  -w /src "$GOLANG_IMAGE" \
  sh -c "go mod tidy && go test ./... -run TestConformance -count=1 -timeout 60m -v -args \
    --gateway-class=titanium \
    --conformance-profiles=GATEWAY-HTTP \
    --supported-features=${SUPPORTED_FEATURES} \
    --cleanup-base-resources=true \
    --organization=justcoding121 \
    --project=titanium-web-proxy \
    --url=https://github.com/justcoding121/titanium-web-proxy \
    --version=${VERSION} \
    --contact=https://github.com/justcoding121/titanium-web-proxy/issues \
    --report-output=/report/$(basename "$REPORT") ${RUN_ARGS[*]:-}"
STATUS=$?
set -e

if [[ $STATUS -ne 0 ]]; then
  echo "Conformance failed. Controller log:" >&2
  kubectl -n "$NAMESPACE" logs -l app.kubernetes.io/component=controller --tail=200 --prefix >&2 || true
fi
rm -f "$KUBECONFIG_FILE"
exit $STATUS
