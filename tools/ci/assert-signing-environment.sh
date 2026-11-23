#!/usr/bin/env bash
# The win-x64 release jobs use the signing environment (azure/login OIDC).
# A pull_request dry-run only reaches that login when the environment's
# deployment branch policy admits the PR branch. The federated credential
# subject for an environment is repo:<owner>/<name>:environment:signing
# and does not include the branch; the branch policy is the gate.
#
# GITHUB_TOKEN often cannot read environments. A 403 is reported and does not
# fail the job: the signing job itself waits or fails if the policy rejects it.
# A readable policy that omits this ref fails here, before the long build.
set -euo pipefail

allow() {
  local policy_json="$1" policies_json="$2" ref_name="$3"
  python3 - "$policy_json" "$policies_json" "$ref_name" <<'PY'
import json, sys
policy_raw, branches_raw, ref = sys.argv[1:]
policy = json.loads(policy_raw) if policy_raw and policy_raw != "null" else None
if policy is None:
    sys.exit(0)
protected = bool(policy.get("protected_branches"))
custom = bool(policy.get("custom_branch_policies"))
if not protected and not custom:
    sys.exit(0)
names = []
if branches_raw and branches_raw != "null":
    data = json.loads(branches_raw)
    for item in data.get("branch_policies") or []:
        names.append(item.get("name") or "")
if "*" in names or ref in names:
    sys.exit(0)
print("policy does not admit", ref, "names=", ",".join(names))
sys.exit(1)
PY
}

if [[ "${1:-}" == "--self-test" ]]; then
  allow "null" "null" "develop"
  if allow '{"protected_branches":false,"custom_branch_policies":true}' '{"branch_policies":[{"name":"develop"}]}' "develop"; then
    echo "develop policy admitted"
  else
    echo "self-test: develop policy should pass" >&2
    exit 1
  fi
  if allow '{"protected_branches":false,"custom_branch_policies":true}' '{"branch_policies":[{"name":"beta"}]}' "develop"; then
    echo "self-test: beta-only policy should reject develop" >&2
    exit 1
  fi
  echo "signing-environment self-test ok"
  exit 0
fi

repo="${GITHUB_REPOSITORY:?}"
ref_name="${SIGNING_REF_NAME:-${GITHUB_HEAD_REF:-${GITHUB_REF_NAME:-}}}"
echo "OIDC subject must include repo:${repo}:environment:signing"
echo "checking signing environment for ref ${ref_name}"

if [[ -z "${GH_TOKEN:-}" ]]; then
  echo "GH_TOKEN unset; skipping the policy read. azure/login on the signing job is the proof."
  exit 0
fi

set +e
env_json="$(gh api "repos/${repo}/environments/signing" 2>/tmp/signing-env.err)"
status=$?
set -e
if [[ "$status" -ne 0 ]]; then
  echo "could not read the signing environment ($(tr '\n' ' ' < /tmp/signing-env.err))"
  echo "If this is a 403, the token cannot administer environments. Dispatch signing-env-probe.yml from a non-release branch to prove azure/login."
  exit 0
fi
policy="$(printf '%s' "$env_json" | python3 -c 'import json,sys; print(json.dumps((json.load(sys.stdin).get("deployment_branch_policy"))))')"
set +e
branches="$(gh api "repos/${repo}/environments/signing/deployment-branch-policies" 2>/dev/null)"
set -e
if ! allow "$policy" "${branches:-null}" "$ref_name"; then
  echo "The signing environment does not admit ${ref_name}." >&2
  echo "Add a deployment branch policy for ${ref_name} (or for all branches) so release-dry-run can sign." >&2
  echo "Federated credential subject: repo:${repo}:environment:signing" >&2
  exit 1
fi
echo "signing environment admits ${ref_name}"
