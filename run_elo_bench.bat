@echo off
setlocal
python "%~dp0tools\fastchess\run_elo_bench.py" %*
endlocal
