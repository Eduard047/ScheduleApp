#!/usr/bin/env bash
set -euo pipefail

project_dir="BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests"
project_file="$project_dir/BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.csproj"
temp_root="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
temp_dir="$(mktemp -d "$temp_root/scheduleapp-compile-items.XXXXXX")"
trap 'rm -rf -- "$temp_dir"' EXIT
tracked_sources="$temp_dir/tracked.txt"
compiled_sources="$temp_dir/compiled.txt"
allowed_exclusions="$temp_dir/allowed-exclusions.txt"
missing_sources="$temp_dir/missing.txt"

git ls-files -- "$project_dir" \
  | sed -nE "/\.cs$/s#^$project_dir/##p" \
  | sort -u > "$tracked_sources"

# Читаємо фактично обчислені MSBuild Compile items, а не лише текстові Include у XML.
dotnet msbuild "$project_file" \
  -nologo \
  -getItem:Compile \
  -p:Configuration=Release \
  -p:EnableLocalAutogenHarness=false \
  | sed -nE 's/^[[:space:]]*"Identity": "([^"]+)",?$/\1/p' \
  | tr '\\' '/' \
  | sed -E 's#/+#/#g' \
  | sort -u > "$compiled_sources"

# Ці локальні відтворювачі навмисно вмикаються лише через EnableLocalAutogenHarness=true.
printf '%s\n' \
  'AutogenL3SeptemberTwoWeekScenarioTests.cs' \
  'AutogenL3Week18DiagnosticsTests.cs' \
  'Infrastructure/TempDatabaseAndCopy.cs' \
  'Infrastructure/WorkspaceAndConfig.cs' \
  | sort -u > "$allowed_exclusions"

comm -23 "$tracked_sources" "$compiled_sources" \
  | comm -23 - "$allowed_exclusions" \
  > "$missing_sources"

if [ -s "$missing_sources" ]; then
  echo "::error::Ці тестові source-файли не включено до integration test project:"
  cat "$missing_sources"
  exit 1
fi

echo "Усі tracked test source-файли компілюються в CI або явно дозволені як локальні harness-файли."
