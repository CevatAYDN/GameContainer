#!/usr/bin/env bash
# Unity 6000.6.8 (Mono + IL2CPP) compile verification for the Nexus demo project.
# Requires: Unity 6000.6.8f1 (Hub install) with the Windows Build Support module.
#
# Usage (from the repo root):
#   UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe" \
#     bash tools/unity-verify/verify-unity-build.sh
#
# Each step is a separate Unity batch process; any failure exits non-zero and
# prints the tail of the relevant log in a fresh evidence folder.
set -Eeuo pipefail

UNITY="${UNITY_PATH:-C:/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe}"
PYTHON="${PYTHON_PATH:-python}"
TOOL="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$(cd "$TOOL/../.." && pwd)/Nexus"
CHECKER="$TOOL/check_results.py"
LOG=""

if [ -n "${UNITY_VERIFY_EVIDENCE_DIR:-}" ]; then
  # mkdir (without -p) is the atomic no-reuse guard, including on failed runs.
  mkdir "$UNITY_VERIFY_EVIDENCE_DIR"
  ARTIFACTS="$(cd "$UNITY_VERIFY_EVIDENCE_DIR" && pwd)"
else
  mkdir -p "$TOOL/artifacts"
  ARTIFACTS="$(mktemp -d "$TOOL/artifacts/run-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
fi
echo "Evidence: $ARTIFACTS"
trap 'rc=$?; echo "Verification FAILED (exit $rc). Evidence retained: $ARTIFACTS"; if [ -f "$LOG" ]; then tail -n 60 "$LOG"; fi; exit "$rc"' ERR

"$PYTHON" -c 'from pathlib import Path; import sys; sys.exit(0 if "m_EditorVersion: 6000.6.8f1" in Path(sys.argv[1]).read_text().splitlines() else "Project must use pinned Unity 6000.6.8f1")' \
  "$PROJECT/ProjectSettings/ProjectVersion.txt"

current_commit() {
  local head dirty
  head="$(git -C "$PROJECT" rev-parse HEAD)"
  [[ "$head" =~ ^[a-fA-F0-9]{40}$ ]]
  dirty="$(git -C "$PROJECT" status --porcelain --untracked-files=all -- Packages Assets)"
  if [ -n "$dirty" ]; then head="$head (Packages or Assets modified)"; fi
  printf '%s' "$head"
}
COMMIT="$(current_commit)"
printf '%s\n' "$COMMIT" > "$ARTIFACTS/source.commit"

build_player() {
  local target="$1" backend="$2" method="$3" started
  [ "$(current_commit)" = "$COMMIT" ]
  LOG="$ARTIFACTS/$target-build.log"
  started="$("$PYTHON" -c 'import time; print(time.time_ns())')"
  "$UNITY" -batchmode -nographics -buildTarget Win64 -projectPath "$PROJECT" \
    -executeMethod "NexusVerify.NexusVerifyBuild.$method" -logFile "$LOG" -quit
  "$PYTHON" "$CHECKER" build "$PROJECT" "$LOG" "$target" "$backend" \
    "$started" "$COMMIT" "$ARTIFACTS/$target-build.json"
  [ "$(current_commit)" = "$COMMIT" ]
}

echo "=== [1/4] EditMode tests (compiles editor + runtime under Mono) ==="
LOG="$ARTIFACTS/editmode.log"
"$UNITY" -batchmode -nographics -buildTarget Win64 -projectPath "$PROJECT" \
  -runTests -testPlatform EditMode \
  -testResults "$ARTIFACTS/editmode-results.xml" \
  -logFile "$LOG"
"$PYTHON" "$CHECKER" tests "$ARTIFACTS/editmode-results.xml"
echo "EditMode OK"

echo "=== [2/4] PlayMode tests (NexusStarter boots, dispatch pipelines run) ==="
LOG="$ARTIFACTS/playmode.log"
"$UNITY" -batchmode -nographics -buildTarget Win64 -projectPath "$PROJECT" \
  -runTests -testPlatform PlayMode \
  -testResults "$ARTIFACTS/playmode-results.xml" \
  -logFile "$LOG"
"$PYTHON" "$CHECKER" tests "$ARTIFACTS/playmode-results.xml"
echo "PlayMode OK"

echo "=== [3/4] Mono player build (StandaloneWindows64) ==="
build_player standalone-mono Mono2x BuildStandaloneMono
echo "Mono build OK"

echo "=== [4/4] IL2CPP player build (StandaloneWindows64 — code stripping + AOT) ==="
build_player standalone-il2cpp IL2CPP BuildStandaloneIL2CPP
echo "IL2CPP build OK"

echo ""
echo "ALL UNITY 6000.6.8 COMPILE-VERIFICATION STEPS PASSED"
echo "Artifacts: $ARTIFACTS"
