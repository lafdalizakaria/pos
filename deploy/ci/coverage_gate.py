"""Fails the build when line coverage of a package (or of everything) is below the threshold.

usage: coverage_gate.py <results-dir> <threshold> [package-name]
Several Cobertura reports may cover the same package: the best rate per class wins (classes are merged by name).
"""
import glob
import sys
import xml.etree.ElementTree as ET

results_dir, threshold = sys.argv[1], float(sys.argv[2])
package = sys.argv[3] if len(sys.argv) > 3 else None
reports = glob.glob(f"{results_dir}/**/coverage.cobertura.xml", recursive=True)
if not reports:
    sys.exit(f"No coverage report found under {results_dir}")

# (class name, line number) -> covered? ; merging reports produced by different test projects.
lines: dict[tuple[str, str, int], bool] = {}
for report in reports:
    for pkg in ET.parse(report).getroot().iter("package"):
        if package and pkg.get("name") != package:
            continue
        for cls in pkg.iter("class"):
            for line in cls.iter("line"):
                key = (pkg.get("name"), cls.get("filename"), int(line.get("number")))
                lines[key] = lines.get(key, False) or int(line.get("hits")) > 0

valid = len(lines)
covered = sum(lines.values())
rate = covered / valid if valid else 0.0
label = package or "all packages"
print(f"Line coverage of {label}: {rate:.1%} ({covered}/{valid}), threshold {threshold:.0%}")
if valid == 0 or rate < threshold:
    sys.exit(1)
