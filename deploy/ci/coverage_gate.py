"""Fails the build when line coverage of the given Cobertura reports is below the threshold."""
import glob
import sys
import xml.etree.ElementTree as ET

results_dir, threshold = sys.argv[1], float(sys.argv[2])
reports = glob.glob(f"{results_dir}/**/coverage.cobertura.xml", recursive=True)
if not reports:
    sys.exit(f"No coverage report found under {results_dir}")

covered = valid = 0
for report in reports:
    root = ET.parse(report).getroot()
    covered += int(root.get("lines-covered"))
    valid += int(root.get("lines-valid"))

rate = covered / valid if valid else 0.0
print(f"Line coverage: {rate:.1%} ({covered}/{valid}), threshold {threshold:.0%}")
if rate < threshold:
    sys.exit(1)
