# Zanak Lichess Bot (`zanak-bot`)

Containerized [`lichess-bot`](https://github.com/lichess-bot-devs/lichess-bot) runner for **Zanak**, ready for local Docker or **Azure Container Instances (ACI)**.

> [!IMPORTANT]
> Never commit your `LICHESS_BOT_TOKEN` to git. Always pass it at runtime via environment variables (or `--secure-environment-variables` in Azure).

---

## Files

- [`Dockerfile`](Dockerfile) — Multi-stage build: compiles `src/Zanak/Zanak.csproj` for `linux-x64` on `.NET 11` and packages it with `lichess-bot`, `networks/*.nnue`, `syzygy/`, and `book.bin`.
- [`config.yml.template`](config.yml.template) — `lichess-bot` configuration tuned for Zanak (`ponder: false`, `Threads: 2`, `Hash: 128`, PolyGlot `rodent.bin` + `book.bin` with `uniform_random` selection up to 6 moves, Syzygy 3–4 piece tablebases).
- [`entrypoint.sh`](entrypoint.sh) — Injects `LICHESS_BOT_TOKEN` into `config.yml` at startup and launches `lichess-bot.py`.
- [`deploy-aci.ps1`](deploy-aci.ps1) — One-command Azure deployment using `az acr build` + `az container create`.

---

## 1. Run Locally with Docker

From the **repository root**:

```powershell
# Build the image
docker build -f tools/lichess-bot/Dockerfile -t zanak-lichess-bot .

# Run the bot (pass token via env var)
docker run --rm -it `
  -e LICHESS_BOT_TOKEN="YOUR_TOKEN_HERE" `
  zanak-lichess-bot
```

---

## 2. Deploy to Azure Container Instances (ACI)

You don't even need a local Docker daemon—`az acr build` builds the image directly in Azure.

1. Log in to Azure:
   ```powershell
   az login
   ```
2. Run the deployment script (pick a globally unique lowercase `AcrName`):
   ```powershell
   $env:LICHESS_BOT_TOKEN = "YOUR_TOKEN_HERE"
   .\tools\lichess-bot\deploy-aci.ps1 `
       -ResourceGroup "zanak-bot-rg" `
       -Location "westeurope" `
       -AcrName "zanakbotacr01" `
       -LichessToken $env:LICHESS_BOT_TOKEN
   ```
3. Watch the live logs:
   ```powershell
   az container logs --resource-group zanak-bot-rg --name zanak-lichess-bot --follow
   ```

### Useful ACI Management Commands

```powershell
# Stop the bot (stops billing while stopped)
az container stop --resource-group zanak-bot-rg --name zanak-lichess-bot

# Start the bot again
az container start --resource-group zanak-bot-rg --name zanak-lichess-bot

# Tear down everything (container, ACR, and resource group)
az group delete --name zanak-bot-rg --yes --no-wait
```

---

## 3. Tuning Notes

- **CPU & Threads:** `config.yml.template` defaults to `Threads: 2` and `concurrency: 1` to match `--cpu 2` on ACI. If you scale ACI to 4 vCPUs, increase `Threads: 4` in `config.yml.template`.
- **Matchmaking:** `allow_matchmaking` is `false` by default (it accepts incoming challenges). Set it to `true` in `config.yml.template` if you want `zanak-bot` to actively seek games on Lichess.
