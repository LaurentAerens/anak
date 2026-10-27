@echo off
REM Anak Weakness Finder - Single Command Campaign Reprocessor & Reporter
.\.venv\Scripts\python -m tools.anak_weakness_finder.main run-all --reprocess-only %*
