#!/usr/bin/env bash
# Run from the repository root. Shared by GitHub Actions and act.
set -euo pipefail
report="${1:-semgrep-report.sarif}"
mkdir -p "$(dirname "$report")"
export SEMGREP_SEND_METRICS=off
export SEMGREP_ENABLE_VERSION_CHECK=0
semgrep --version
semgrep --validate --strict --config .semgrep --metrics off
# Explicitly pair the legacy .yml filename: automatic discovery tested only .yaml.
semgrep --test --config .semgrep/csharp-security.yml .semgrep/csharp-security.cs --metrics off
semgrep --test .semgrep/vendor --metrics off
# Fixtures in .semgrep are exercised above; scan application AND xUnit source.
# --strict fails on analysis errors; --error fails on security findings.
semgrep scan --config .semgrep --severity ERROR --strict \
  --exclude '**/bin/**' --exclude '**/obj/**' \
  --metrics off --disable-version-check --no-rewrite-rule-ids \
  --sarif --output "$report" --error \
  FileManager.Server FileManager.Client FileManager.Tests
