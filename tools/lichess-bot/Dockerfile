# Build context: repository root
#   docker build -f tools/lichess-bot/Dockerfile -t zanak-lichess-bot .

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Copy build props and project files first for layer caching
COPY Directory.Build.props ./
COPY src/Anak.Core/Anak.Core.csproj src/Anak.Core/
COPY src/Anak.Data/Anak.Data.csproj src/Anak.Data/
COPY src/Anak.Search/Anak.Search.csproj src/Anak.Search/
COPY src/Zanak/Zanak.csproj src/Zanak/

RUN dotnet restore src/Zanak/Zanak.csproj -r linux-x64

# Copy source and required engine assets
COPY src/Anak.Core/ src/Anak.Core/
COPY src/Anak.Data/ src/Anak.Data/
COPY src/Anak.Search/ src/Anak.Search/
COPY src/Zanak/ src/Zanak/
COPY networks/ networks/

RUN dotnet publish src/Zanak/Zanak.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -o /out/engine

FROM python:3.12-slim AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Install lichess-bot
RUN git clone --depth 1 https://github.com/lichess-bot-devs/lichess-bot.git /app/lichess-bot \
    && pip install --no-cache-dir -r /app/lichess-bot/requirements.txt

# Copy published Zanak engine (includes networks/*.nnue), books, and Syzygy tablebases
COPY --from=build /out/engine /app/engine
COPY book.bin /app/engine/book.bin
RUN python3 -c "import urllib.request; urllib.request.urlretrieve('https://raw.githubusercontent.com/michaeldv/donna_opening_books/master/rodent.bin', '/app/engine/rodent.bin')"
COPY syzygy/ /app/engine/syzygy/

# Copy bot configuration template and entrypoint
COPY tools/lichess-bot/config.yml.template /app/config.yml.template
COPY tools/lichess-bot/entrypoint.sh /app/entrypoint.sh
RUN sed -i 's/\r$//' /app/entrypoint.sh && chmod +x /app/entrypoint.sh

ENV PYTHONUNBUFFERED=1

ENTRYPOINT ["/app/entrypoint.sh"]
