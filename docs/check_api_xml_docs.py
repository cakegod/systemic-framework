#!/usr/bin/env python3
"""Check generated XML docs for the system-author API contract and rule help anchors."""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

LIBRARIES = {
    "Systemic.Kernel": "src/Systemic.Kernel",
    "Systemic.Core": "src/Systemic.Core",
    "Systemic.Kernel.Generator": "src/Systemic.Kernel.Generator",
    "Systemic.Rendering": "src/modules/Systemic.Rendering",
    "Systemic.Incremental": "src/modules/Systemic.Incremental",
    "Systemic.Presentation": "src/modules/Systemic.Presentation",
    "Systemic.Gameplay2D": "src/modules/Systemic.Gameplay2D",
}

# Each entry is an exact XML documentation ID or a method prefix ending at the opening parenthesis.
REQUIRED_MEMBERS = {
    "Systemic.Core": [
        "T:Systemic.Host.ISystemicSystem",
        "P:Systemic.Host.ISystemicSystem.Id",
        "P:Systemic.Host.ISystemicSystem.Order",
        "P:Systemic.Host.ISystemicSystem.Access",
        "M:Systemic.Host.ISystemicSystem.Step(",
        "T:Systemic.Host.SystemicSystemContext",
        "P:Systemic.Host.SystemicSystemContext.Tick",
        "P:Systemic.Host.SystemicSystemContext.WorkerCount",
        "P:Systemic.Host.SystemicSystemContext.World",
        "P:Systemic.Host.SystemicSystemContext.Commands",
        "P:Systemic.Host.SystemicSystemContext.IsScheduledExecution",
        "M:Systemic.Host.SystemicSystemContext.GetService``1",
        "M:Systemic.Host.SystemicSystemContext.GetWritableWorld",
        "M:Systemic.Host.SystemicSystemContext.SetProperty(",
        "M:Systemic.Host.SystemicSystemContext.Publish(",
        "M:Systemic.Host.SystemicSystemContext.OnRollback(",
        "M:Systemic.Host.SystemicSystemContext.ForEachRange``1(",
        "M:Systemic.Host.SystemicSystemContext.ReduceRanges``2(",
        "M:Systemic.Host.SystemicSystemContext.BorrowPopulation``1(",
        "M:Systemic.Host.SystemicSystemContext.DeferPopulationCommands``1(",
        "M:Systemic.Host.SystemicSystemContext.ForEachPopulationRange``2(",
        "T:Systemic.Host.SystemicGameBuilder",
        "M:Systemic.Host.SystemicGameBuilder.AddSystem(",
        "M:Systemic.Host.SystemicGameBuilder.WithScheduledWorkerCount(",
        "M:Systemic.Host.SystemicGameBuilder.Build",
        "T:Systemic.Rules.PopulationSink",
        "M:Systemic.Rules.PopulationSink.#ctor(",
        "M:Systemic.Rules.PopulationSink.Collect(",
        "M:Systemic.Rules.RuleEngine.EvaluateRowEvent(",
        "T:Systemic.Rules.RuleRowScanResult",
    ],
    "Systemic.Kernel": [
        "T:Systemic.Kernel.SystemAccess",
        "P:Systemic.Kernel.SystemAccess.ReadWorld",
        "P:Systemic.Kernel.SystemAccess.WriteWorld",
        "P:Systemic.Kernel.SystemAccess.DeferWorldWrites",
        "T:Systemic.Kernel.IScheduledKernelSystem",
        "P:Systemic.Kernel.IScheduledKernelSystem.Id",
        "P:Systemic.Kernel.IScheduledKernelSystem.Access",
        "M:Systemic.Kernel.IScheduledKernelSystem.Execute(",
        "T:Systemic.Kernel.DeterministicSystemScheduleBuilder",
        "M:Systemic.Kernel.DeterministicSystemScheduleBuilder.#ctor(",
        "M:Systemic.Kernel.DeterministicSystemScheduleBuilder.Add(",
        "M:Systemic.Kernel.DeterministicSystemScheduleBuilder.Build",
        "T:Systemic.Kernel.DeterministicSystemSchedule",
        "P:Systemic.Kernel.DeterministicSystemSchedule.WorkerCount",
        "P:Systemic.Kernel.DeterministicSystemSchedule.SystemCount",
        "P:Systemic.Kernel.DeterministicSystemSchedule.Waves",
        "M:Systemic.Kernel.DeterministicSystemSchedule.Execute(",
        "M:Systemic.Kernel.DeterministicSystemSchedule.Dispose",
        "T:Systemic.Kernel.ScheduledSystemContext",
        "P:Systemic.Kernel.ScheduledSystemContext.Tick",
        "P:Systemic.Kernel.ScheduledSystemContext.World",
        "P:Systemic.Kernel.ScheduledSystemContext.Commands",
        "P:Systemic.Kernel.ScheduledSystemContext.WorkerCount",
        "M:Systemic.Kernel.ScheduledSystemContext.BorrowPopulation``1(",
        "M:Systemic.Kernel.ScheduledSystemContext.DeferPopulationCommands``1(",
        "M:Systemic.Kernel.ScheduledSystemContext.OnRollback(",
        "M:Systemic.Kernel.ScheduledSystemContext.ForEachRange``1(",
        "M:Systemic.Kernel.ScheduledSystemContext.ReduceRanges``2(",
        "M:Systemic.Kernel.ScheduledSystemContext.ForEachPopulationRange``2(",
        "T:Systemic.Kernel.IPopulationKind",
        "T:Systemic.Kernel.PopulationRange",
        "T:Systemic.Kernel.Population`1",
        "M:Systemic.Kernel.Population`1.Borrow",
        "M:Systemic.Kernel.Population`1.Reserve(",
        "M:Systemic.Kernel.Population`1.AddRange(",
        "M:Systemic.Kernel.Population`1.RemoveSwapBack(",
        "M:Systemic.Kernel.Population`1.Column``1(",
        "M:Systemic.Kernel.Population`1.ReferenceOf(",
        "M:Systemic.Kernel.Population`1.DoubleBufferedColumn``1(",
        "T:Systemic.Kernel.PopulationLease`1",
        "M:Systemic.Kernel.Population`1.WithAccess``1(",
        "M:Systemic.Kernel.Population`1.WithAccess``2(",
        "M:Systemic.Kernel.PopulationLease`1.WithAccess``1(",
        "M:Systemic.Kernel.PopulationLease`1.WithAccess``2(",
        "T:Systemic.Kernel.PopulationAccessScope`1",
        "P:Systemic.Kernel.PopulationAccessScope`1.Range",
        "P:Systemic.Kernel.PopulationAccessScope`1.Guard",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadAll``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadAll``2(",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadRange``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.EditRange``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadCurrent``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadCurrent``2(",
        "M:Systemic.Kernel.PopulationAccessScope`1.ReadCurrentRange``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.EditNext``1(",
        "M:Systemic.Kernel.PopulationAccessScope`1.EditBoth``1(",
        "T:Systemic.Kernel.PopulationAccessGuard",
        "M:Systemic.Kernel.PopulationAccessGuard.Validate",
        "T:Systemic.Kernel.PopulationCommandBuffer`1",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.Reserve(",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.Spawn(",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.RemoveRow(",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.Remove(",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.Flush",
        "M:Systemic.Kernel.PopulationCommandBuffer`1.Clear",
        "T:Systemic.Kernel.PopulationSchemaAttribute",
        "P:Systemic.Kernel.PopulationSchemaAttribute.Id",
        "P:Systemic.Kernel.PopulationSchemaAttribute.Version",
        "P:Systemic.Kernel.PopulationSchemaAttribute.EnableHandles",
        "T:Systemic.Kernel.PopulationColumnAttribute",
        "P:Systemic.Kernel.PopulationColumnAttribute.Id",
        "P:Systemic.Kernel.PopulationColumnAttribute.DoubleBuffered",
        "P:Systemic.Kernel.PopulationColumnAttribute.CodecType",
    ],
}

REQUIRED_RULE_ANCHORS = (
    "sysdet001",
    "sysdet002",
    "sysdet003",
    "sysacc001",
    "sysacc002",
    "sysacc003",
    "sysacc004",
    "sysacc005",
    "sysacc006",
)


def find_xml_file(project: str, assembly: str, configuration: str) -> Path | None:
    output = ROOT / project / "bin" / configuration
    matches = sorted(output.glob(f"**/{assembly}.xml"))
    return matches[0] if len(matches) == 1 else None


def member_matches(names: set[str], required: str) -> list[str]:
    if required.endswith("("):
        return sorted(name for name in names if name.startswith(required))
    if "``" in required and not required.endswith(")"):
        return sorted(name for name in names if name == required or name.startswith(required + "("))
    return [required] if required in names else []


def check_xml_docs(configuration: str) -> list[str]:
    errors: list[str] = []
    xml_by_assembly: dict[str, dict[str, ET.Element]] = {}

    for assembly, project in LIBRARIES.items():
        path = find_xml_file(project, assembly, configuration)
        if path is None:
            errors.append(
                f"expected one generated XML file for {assembly} under {project}/bin/{configuration}"
            )
            continue
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exception:
            errors.append(f"invalid XML documentation in {path.relative_to(ROOT)}: {exception}")
            continue
        xml_by_assembly[assembly] = {
            element.attrib["name"]: element
            for element in root.findall("./members/member")
            if "name" in element.attrib
        }

    for assembly, required_members in REQUIRED_MEMBERS.items():
        members = xml_by_assembly.get(assembly)
        if members is None:
            continue
        names = set(members)
        for required in required_members:
            matches = member_matches(names, required)
            if not matches:
                errors.append(f"{assembly}: missing XML API entry {required}")
                continue
            for name in matches:
                summary = members[name].find("summary")
                summary_text = "" if summary is None else " ".join("".join(summary.itertext()).split())
                if not summary_text:
                    errors.append(f"{assembly}: missing summary for {name}")

    return errors


def check_analyzer_anchors() -> list[str]:
    errors: list[str] = []
    guide_path = ROOT / "docs/api/analyzers.md"
    guide = guide_path.read_text(encoding="utf-8")
    anchors = set(re.findall(r'<a\s+id="([a-z0-9]+)"\s*>\s*</a>', guide))

    for anchor in REQUIRED_RULE_ANCHORS:
        if anchor not in anchors:
            errors.append(f"analyzer guide is missing stable rule anchor #{anchor}")

    analyzer_sources = (
        ROOT / "src/Systemic.Kernel.Generator/StrictDeterminismAnalyzer.cs",
        ROOT / "src/Systemic.Kernel.Generator/SystemAccessAnalyzer.cs",
    )
    for source_path in analyzer_sources:
        source = source_path.read_text(encoding="utf-8")
        for fragment in re.findall(r'helpLinkUri:\s*Guide\s*\+\s*"#([a-z0-9]+)"', source):
            if fragment not in anchors:
                errors.append(
                    f"{source_path.relative_to(ROOT)} links to missing analyzer guide anchor #{fragment}"
                )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", default="Release")
    arguments = parser.parse_args()

    errors = check_xml_docs(arguments.configuration) + check_analyzer_anchors()
    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    print("XML API summaries and analyzer help anchors are present.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
