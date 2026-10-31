# Anak AI Agent Operational Rules & Benchmarking Policy

See full specifications in [`AGENTS.md`](file:///c:/Users/laerens/source/repos/Personal/anak/AGENTS.md).

## Quick Summary for Agents
1. **NO state-modifying Git operations** (`commit`, `checkout`, `switch`, `push`, `branch`, `reset`, `restore`, `stash`) without explicit user permission.
2. **Always run unit tests** (`dotnet test -c Release`) and ensure 100% passing tests before completing any task.
3. **Pure NPS / Speedup Changes (Category A):** Validate with unit tests + identical node count and identical move determinism on benchmark positions (`anak bench`) + 0 GC allocations, enabling fast-track verification without 8k-game matches.
4. **Search / Positional Changes (Category B):** Use streamlined direct match validation with `--early-stop` / SPRT against the active baseline:
   - Early stop terminates regressions/flops quickly (~400–600 games).
   - SPRT terminates winners early ($1,500 - 2,500$ games).
   - Eliminates redundant intermediate 250-game gate delays.
