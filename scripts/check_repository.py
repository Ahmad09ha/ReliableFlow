#!/usr/bin/env python3
"""Offline checks for archive completeness and XML/JSON/CI wiring.
Does NOT replace dotnet build/test or Docker E2E tests.
"""
from pathlib import Path
import json
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
expected = [
    "ReliableFlow.slnx",
    "Directory.Build.props",
    "Directory.Packages.props",
    "src/ReliableFlow.EntityFrameworkCore/ReliableFlow.EntityFrameworkCore.csproj",
    "samples/Ordering.Api/Ordering.Api.csproj",
    "samples/Inventory.Service/Inventory.Service.csproj",
    "samples/Shared.Contracts/Shared.Contracts.csproj",
    "tests/ReliableFlow.Tests/ReliableFlow.Tests.csproj",
    "tests/ReliableFlow.PostgresStress/ReliableFlow.PostgresStress.csproj",
    "compose.yaml",
    "scripts/e2e.py",
    "scripts/e2e_hard_crash.py",
    "scripts/Run-LocalPostgresStress.ps1",
    "scripts/Run-PackageSmoke.ps1",
    "scripts/Run-LocalBrokerLab.ps1",
    "docs/assets/architecture-cover.svg",
    ".github/workflows/ci.yml",
    "docs/ARCHITECTURE.md",
    "docs/FAILURE-MATRIX.md",
    "docs/ADOPTION.md",
    "docs/OPERATIONS.md",
    "docs/VERIFICATION.md",
    "docs/EXPLAINED_AR.md",
    "docs/PUBLISH_TO_GITHUB.md",
    "LINKEDIN_POST.md",
]
for item in expected:
    assert (root / item).is_file(), f"Missing: {item}"

# Build outputs and isolated local failure-lab logs are intentionally excluded
# from Git. Check only source/configuration files, not incidental temp JSON.
ignored_dirs = {".git", ".idea", ".local-test", "bin", "obj", "artifacts", "__pycache__"}
def is_source(path):
    return not ignored_dirs.intersection(path.relative_to(root).parts)

for item in root.rglob("*.csproj"):
    if not is_source(item):
        continue
    ET.parse(item)
    project_root = ET.parse(item).getroot()
    for ref in project_root.iter("ProjectReference"):
        target = (item.parent / ref.attrib["Include"]).resolve()
        assert target.is_file(), f"Broken ProjectReference: {item} -> {target}"
for item in root.rglob("*.props"):
    if not is_source(item):
        continue
    ET.parse(item)
for item in root.rglob("*.json"):
    if not is_source(item):
        continue
    try:
        json.loads(item.read_text(encoding="utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise AssertionError(f"Invalid source JSON {item}: {error}") from error
ET.parse(root / "ReliableFlow.slnx")
print("PASS: repository files, project references, XML and JSON structures")
print("NOT TESTED: C# compilation, NuGet restore, real DB/broker, runtime behavior")
