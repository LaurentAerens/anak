# Anak Weakness Finder - Single Command Campaign Reprocessor & Reporter
param(
    [int]$OracleLimit = 0
)

$argsList = @("run-all", "--reprocess-only")
if ($OracleLimit -gt 0) {
    $argsList += "--oracle-limit", $OracleLimit
}

& ".\.venv\Scripts\python.exe" -m tools.anak_weakness_finder.main @argsList
