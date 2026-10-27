@echo off
REM Anak Weakness Finder - Fresh End-to-End Analysis Campaign (Wipes old data and runs clean)
.\.venv\Scripts\python -m tools.anak_weakness_finder.main run-all --reset --no-resume --concurrency 14 %*
