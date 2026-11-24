# Anak v1.2.1 — Multi-Threaded `NnuePath` Reload Fix

**Release Date:** 2026-11-24  
**Category:** Bugfix (Patch Release)

## Summary

Fixes a `System.AccessViolationException` in `NnueFinnyTable.EnsureInitialized` on helper threads (`RunHelperSearch`) when `setoption name NnuePath` is sent after `Threads > 1`.

## Root Cause

`MultiTierNnueEvaluator.CreateWorkerInstance()` previously copied `_weights` (and `UseNnue`, `NnueDirectory`, `Tablebase`, `Routing`) by value from the parent evaluator. When `setoption name NnuePath` triggered `LoadFromDirectory()` on the parent after worker threads had already been initialized, the parent disposed the previous `NnueWeights` native buffers and assigned the new `NnueWeights`, leaving helper-thread evaluators with a dangling `NnueWeights` reference pointing to freed native memory.

## Fix

- Updated `MultiTierNnueEvaluator` so worker instances retain a `_parent` reference and read `CurrentWeights`, `UseNnue`, `NnueDirectory`, `Tablebase`, and `Routing` dynamically from the parent.
- Swapped `_weights` (marked `volatile`) to the newly loaded `NnueWeights` before disposing the previous instance.
- Added regression tests in `NnueV2Tests` and `AlphaBetaTests` verifying both evaluator weight/Finny-table rebinding and end-to-end 4-thread UCI search after `setoption name NnuePath`.
