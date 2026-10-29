### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PTBK001 | PopulationSchema | Error | Invalid population schema
SYSDET001 | Determinism | Error | Floating-point or non-portable math in strict simulation
SYSDET002 | Determinism | Error | Nondeterministic clock or random source in strict simulation
SYSDET003 | Determinism | Error | Unordered or uncoordinated parallel work in strict simulation
SYSACC001 | SystemAccess | Error | Immediate World operations require SystemAccess.WriteWorld
SYSACC002 | SystemAccess | Error | Deferred population commands require deferred or exclusive World write access
SYSACC003 | SystemAccess | Error | Direct population edits are forbidden inside ForEachRange callbacks
SYSACC004 | SystemAccess | Error | Direct generated column reads and writes require matching per-column access declarations
SYSACC005 | SystemAccess | Error | Immediate World and shared command-buffer mutations are unsafe inside ForEachRange callbacks
SYSACC006 | SystemAccess | Error | Full single-buffered population reads conflict with parallel writes to the column
SYSACC007 | SystemAccess | Info | A system without an Access declaration runs exclusively
SYSACC008 | SystemAccess | Error | Deferred facts require deferred or exclusive World write access
