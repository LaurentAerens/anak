# Anak AI Agent Operational Rules & Benchmarking Policy

See full specifications in [`AGENTS.md`](file:///c:/Users/laerens/source/repos/Personal/anak/AGENTS.md).

## Quick Summary for Agents
1. **NO state-modifying Git operations** (`commit`, `checkout`, `switch`, `push`, `branch`, `reset`, `restore`, `stash`) without explicit user permission.
   - *Exception:* When the user explicitly requests a release, execute the automated release sequence: stage files, commit release, create annotated tag, and push commit + tags.
2. **Always run unit tests** (`dotnet test -c Release`) and ensure 100% passing tests before completing any task.
3. **Follow the 3-Tier Testing Pyramid & Release Policy**:
   - **Patch releases (`x.y.Z`):** CCRL/SPRT ratings recommended, not required (e.g. pure NPS/throughput, bug fixes, zero-regression equivalence).
   - **Major/Minor releases (`X.y.z` / `x.Y.z`):** CCRL/SPRT ratings required.
   - **Tier 1 (250 games):** Quick sanity check. Auto-revert only on hard regression (< 47%). Neutral (48%-52%) is not auto-rejected.
   - **Tier 2 (SPRT [0, 3]):** Fast validation up to 8,000 games that stops early as soon as statistical significance is reached ($1,500-2,500$ games on winners).
   - **Tier 3 (MTC / Fixed Nodes):** For positional/eval/depth features, test at moderate time control (`3+0.03` or `nodes 50000`).
