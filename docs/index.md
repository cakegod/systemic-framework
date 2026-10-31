# Systemic Framework API

The [agent-first plan](agent-first-plan.md) is the current delivery plan. These guides cover
the design bar first, then the host system context and the population data paths through
access declarations, leases, range work, and scheduled changes.

The design target is systemic, synergetic play: general rules that combine into strategies
the developer did not enumerate, not a larger authored choice tree.

Behavior is C# systems and commands. JSON is data. See
[ADR 0004](adr/0004-code-first-simulation.md).

## Guides

- [Design for expressive agency and systemic emergence](design/expressive-agency.md) before
  adding or reviewing a choice, talent, upgrade, merge branch, unlock, or mechanic.
- [Use `SystemicSystemContext`](api/system-context.md) for host and Kernel operations during
  a system step.
- [Build populations and schedules](api/populations-and-scheduling.md) for generated
  columns, safe row access, and deferred population commands.
- [Read analyzer diagnostics](api/analyzers.md) for determinism and system-access rules,
  their activation, and their limits.

The generated API reference is rebuilt from the source tree after the code-first migration.
Contributor guidance explains which XML summaries are required and how to extend that
checked surface.
