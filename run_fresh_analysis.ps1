# Anak Weakness Finder - Fresh End-to-End Analysis Campaign (Wipes old data and runs clean)
param(
    [int]$StartElo = 1600,
    [int]$Games = 10000,
    [int]$Concurrency = 14
)

& ".\.venv\Scripts\python.exe" -m tools.anak_weakness_finder.main run-all --reset --no-resume --start-elo $StartElo --games $Games --concurrency $Concurrency
