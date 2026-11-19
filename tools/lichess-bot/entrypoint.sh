#!/usr/bin/env sh
set -eu

if [ -z "${LICHESS_BOT_TOKEN:-}" ]; then
  echo "ERROR: LICHESS_BOT_TOKEN environment variable is not set." >&2
  exit 1
fi

CONFIG_PATH="/app/lichess-bot/config.yml"

# Substitute token safely without exposing it in logs or image layers
python3 - <<'PY'
import os
from pathlib import Path

token = os.environ["LICHESS_BOT_TOKEN"]
template = Path("/app/config.yml.template").read_text(encoding="utf-8")
Path("/app/lichess-bot/config.yml").write_text(
    template.replace("${LICHESS_BOT_TOKEN}", token),
    encoding="utf-8",
)
PY

chmod 600 "$CONFIG_PATH"

cd /app/lichess-bot
exec python3 lichess-bot.py --config "$CONFIG_PATH"
