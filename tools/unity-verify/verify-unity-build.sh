#!/usr/bin/env bash
# Unity 6000.5.6 (Mono + IL2CPP) compile verification for the Nexus demo project.
# Requires: Unity 6000.5.6f1 (Hub install) with the Windows Build Support module.
# Optional: Android Build Support (SDK + NDK) for the Android IL2CPP step.
#
# Usage (from the repo root):
#   UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.5.6f1/Editor/Unity.exe" \
#     bash tools/unity-verify/verify-unity-build.sh
#
# Each step is a separate Unity batch process; any failure exits non-zero and
# prints the tail of the relevant log under tools/unity-verify/artifacts/.
set -euo pipefail

UNITY="${UNITY_PATH:-C:/Program Files/Unity/Hub/Editor/6000.5.6f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)/Nexus"
ARTIFACTS="$(cd "$(dirname "$0")" && pwd)/artifacts"
mkdir -p "$ARTIFACTS"

require_passed_test_results() {
  local results="$1"
  local label="$2"
  if [ ! -f "$results" ]; then
    echo "$label tests produced no result XML: $results"
    return 1
  fi
  if ! grep -q '<test-run' "$results" || ! grep -q 'result="Passed"' "$results"; then
    echo "$label tests did not report an overall Passed result"
    return 1
  fi
}

echo "=== [1/4] EditMode tests (compiles editor + runtime under Mono) ==="
"$UNITY" -batchmode -nographics -projectPath "$PROJECT" \
  -runTests -testPlatform EditMode \
  -testResults "$ARTIFACTS/editmode-results.xml" \
  -logFile "$ARTIFACTS/editmode.log" -quit
require_passed_test_results "$ARTIFACTS/editmode-results.xml" "EditMode"
if grep -q 'result="Failed"' "$ARTIFACTS/editmode-results.xml"; then
  echo "EditMode tests FAILED"; tail -n 60 "$ARTIFACTS/editmode.log"; exit 1
fi
echo "EditMode OK"

echo "=== [2/4] PlayMode tests (NexusStarter boots, dispatch pipelines run) ==="
"$UNITY" -batchmode -nographics -projectPath "$PROJECT" \
  -runTests -testPlatform PlayMode \
  -testResults "$ARTIFACTS/playmode-results.xml" \
  -logFile "$ARTIFACTS/playmode.log" -quit
require_passed_test_results "$ARTIFACTS/playmode-results.xml" "PlayMode"
if grep -q 'result="Failed"' "$ARTIFACTS/playmode-results.xml"; then
  echo "PlayMode tests FAILED"; tail -n 60 "$ARTIFACTS/playmode.log"; exit 1
fi
echo "PlayMode OK"

echo "=== [3/4] Mono player build (StandaloneWindows64) ==="
"$UNITY" -batchmode -nographics -projectPath "$PROJECT" \
  -executeMethod NexusVerify.Build.BuildStandaloneMono \
  -logFile "$ARTIFACTS/mono-build.log" -quit
if [ ! -d "$PROJECT/builds/standalone-mono" ]; then
  echo "Mono build output missing"; tail -n 60 "$ARTIFACTS/mono-build.log"; exit 1
fi
echo "Mono build OK"

echo "=== [4/4] IL2CPP player build (StandaloneWindows64 — code stripping + AOT) ==="
"$UNITY" -batchmode -nographics -projectPath "$PROJECT" \
  -executeMethod NexusVerify.Build.BuildStandaloneIL2CPP \
  -logFile "$ARTIFACTS/il2cpp-build.log" -quit
if [ ! -d "$PROJECT/builds/standalone-il2cpp" ]; then
  echo "IL2CPP build output missing"; tail -n 60 "$ARTIFACTS/il2cpp-build.log"; exit 1
fi
echo "IL2CPP build OK"

echo ""
echo "ALL UNITY 6000.5.6 COMPILE-VERIFICATION STEPS PASSED"
echo "Artifacts: $ARTIFACTS"
