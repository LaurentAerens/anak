# Credits & Third-Party Assets

Anak acknowledges and credits the creators, researchers, and open-source chess community members whose datasets, formats, and tools enhance this chess engine.

---

## 1. Opening Book: `Performance.bin`

- **Curator / Author**: **Marc Lacrosse** (Belgian FIDE Master and longtime contributor to chess software, Scid, and opening theory).
- **Format**: Standard PolyGlot binary opening book (`.bin`, 16 bytes per entry, 92,954 master entries, ~1.45 MB).
- **Content**: Thorough coverage of grandmaster opening theory across all major starting moves (`1.e4`, `1.d4`, `1.c4`, `1.Nf3`), Sicilian lines (Najdorf, Dragon, Sveshnikov, Rossolimo), French and Caro-Kann structures, Queen's Gambit, Indian Defenses (King's Indian, Nimzo-Indian, Grünfeld), and flank openings.
- **License**: Permissive / BSD-compatible distribution (widely bundled across open-source chess engines, Scid, and `python-chess`).
- **Engine Probing**: Evaluated at engine root via `Anak.Data.PolyGlot.PolyGlotBook` with configurable selection modes (`WeightedRandom`, `BestMove`, `UniformRandom`).

---

## 2. Endgame Tablebases: Syzygy 3-Piece & 4-Piece (`.rtbw` & `.rtbz`)

- **Author & Inventor**: **Ronald de Man** ([syzygy1](https://github.com/syzygy1))
- **Format**: Syzygy Win-Draw-Loss (`.rtbw`) and Distance-to-Zero (`.rtbz`) endgame tablebases.
- **Files Shipped with Anak**: Complete 3-piece and 4-piece endgame tablebase suite (35 WDL files + 35 DTZ files = 70 files total, ~4.14 MB):
  - **3-Piece Tables** (10 files): `KBvK`, `KNvK`, `KPvK`, `KQvK`, `KRvK` (`.rtbw` & `.rtbz`)
  - **4-Piece Tables** (60 files): Complete coverage of all 4-piece endgame combinations, including `KBBvK`, `KBNvK`, `KBPvK`, `KBvKB`, `KBvKN`, `KBvKP`, `KNNvK`, `KNPvK`, `KNvKN`, `KNvKP`, `KPPvK`, `KPvKP`, `KQBvK`, `KQNvK`, `KQPvK`, `KQQvK`, `KQRvK`, `KQvKB`, `KQvKN`, `KQvKP`, `KQvKQ`, `KQvKR`, `KRBvK`, `KRNvK`, `KRPvK`, `KRRvK`, `KRvKB`, `KRvKN`, `KRvKP`, `KRvKR` (`.rtbw` & `.rtbz`).
- **Hosting & Public Mirrors**:
  - Main mirror: [http://tablebase.sesse.net/syzygy/](http://tablebase.sesse.net/syzygy/) (hosted by Steinar H. Gunderson / Sesse)
  - Cygni mirror: [https://chess.cygni.se/syzygy/](https://chess.cygni.se/syzygy/)
- **Engine Implementation**: Pure managed C# Syzygy prober (`Anak.Data.Syzygy.SyzygyWdl` and `Anak.Data.Syzygy.SyzygyDtz`) built with zero unmanaged DLL dependencies. Automatically discovers `./syzygy` locally or obeys the UCI `SyzygyPath` option.

### Extended 5-Piece & 6-Piece Syzygy Tablebases
Users who require 5-piece (~940 MB) or 6-piece (~150 GB) Syzygy tablebases can download the tables from the mirrors above and configure Anak via the standard UCI option:
```uci
setoption name SyzygyPath value /path/to/syzygy/
```

---

## 3. Tooling & Community Benchmarks

- **fast-chess**: Automated tournament runner and SPRT engine test suite ([fast-chess on GitHub](https://github.com/Disservin/fast-chess)).
- **python-chess**: Python chess library used for dataset validation, verification, and preprocessing ([niklasf/python-chess](https://github.com/niklasf/python-chess)).
