# Zanak `v1.1.1` — In-Check Mate-in-1 Root Filter & UCI Version Fix

`zanak-v1.1.1` is a patch release for **Zanak** (ZAP — Zero-branching Accelerated Probe).

## Changes in `v1.1.1`

* **In-Check Mate-in-1 Root Filtering (`ZapRootFilter`):**
  * Fixed `ZapRootFilter.Filter` returning `0` early when `root.IsInCheck()` is true, which previously skipped `ZapPolicy.AllowsMateIn1` on check-evasion moves.
  * When in check, `ZapRootFilter.Filter` now rejects evasions that walk into an immediate mate-in-1 while preserving all moves if every legal evasion allows mate-in-1.
* **UCI Identity String (`UciHandler`):**
  * Updated `id name` to `Zanak 1.1.1` to match `Zanak.csproj` (previously remained `Zanak 1.0.0`).
* **Unit Tests (`ZapRootFilterTests`):**
  * Added `InCheck_RejectsEvasionsThatAllowMateIn1` and `InCheck_NeverRejectsAllEvasionsWhenAllAllowMateIn1`.
