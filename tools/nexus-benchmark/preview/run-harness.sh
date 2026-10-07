#!/bin/sh
# Base44 preview job: build and run the Nexus benchmark harness, then publish its
# report into the shared /report volume that the nginx "preview" service serves.
#
# The report directory is the nginx web root (preview/index.html is bind-mounted into
# it by docker-compose.base44.yml), so everything the viewer needs is published there:
#   raw.log           - console output of the run, followed by the --json document
#   build.log         - the MSBuild output
#   exitcode          - the harness exit code (0 = every suite passed)
#   generated-at.txt  - UTC timestamp of the run
#
# This script deliberately exits 0 as soon as the report is written (even when the
# harness fails): the report itself carries pass/fail, and the viewer must come up
# so failures are visible instead of leaving the preview blank.
set -u

REPORT_DIR="${REPORT_DIR:-/report}"
mkdir -p "$REPORT_DIR"

{
  echo "===== dotnet build -c Release ====="
  dotnet build -c Release --nologo
} > "$REPORT_DIR/build.log" 2>&1
build_code=$?

if [ "$build_code" -eq 0 ]; then
  {
    echo "===== dotnet run -c Release --no-build -- --json ====="
    dotnet run -c Release --no-build -- --json
  } > "$REPORT_DIR/raw.log" 2>&1
  run_code=$?
else
  run_code=$build_code
  echo "===== dotnet build failed (exit $build_code) - see build.log =====" > "$REPORT_DIR/raw.log"
fi

echo "$run_code" > "$REPORT_DIR/exitcode"
date -u +"%Y-%m-%dT%H:%M:%SZ" > "$REPORT_DIR/generated-at.txt"
echo "harness build exit=$build_code, run exit=$run_code (report in $REPORT_DIR)"
exit 0
