#!/usr/bin/env bash
# Fail when any project references a package (direct or transitive) with a known
# vulnerability. `dotnet list package --vulnerable` exits 0 even when it finds some,
# so the JSON report is inspected instead. Needs a prior restore and network access
# to the NuGet vulnerability feed.
set -euo pipefail
cd "$(dirname "$0")/../.."
report=$(dotnet list Nightware.slnx package --vulnerable --include-transitive --format json)
python3 - "$report" <<'PY'
import json, sys
report = json.loads(sys.argv[1])
found = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                for vulnerability in package.get("vulnerabilities", []):
                    found.append(f"{project['path'].split('/')[-1]}: {package['id']} {package.get('resolvedVersion', '?')} "
                                 f"[{vulnerability.get('severity')}] {vulnerability.get('advisoryurl')}")
for problem in report.get("problems", []):
    print(f"warning: {problem.get('text')}", file=sys.stderr)
if found:
    print("Vulnerable packages:", *sorted(set(found)), sep="\n  ", file=sys.stderr)
    sys.exit(1)
print("No vulnerable packages.")
PY
