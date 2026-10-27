#!/usr/bin/env python3
"""
Auto-generated Alpha-Beta Search Regression Repro Suite.
Executes Anak micro-searches on critical blunder positions to diagnose pruning/horizon bugs.
"""

import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
TEST_FEN_SCRIPT = REPO_ROOT / "tools" / "test_fen.py"

CRITICAL_POSITIONS = [
    {
        "game": "tier_sf1800_1793043025_g2648",
        "opening": "Queen's Pawn Game",
        "move_num": 22,
        "blunder_move": "Ne2",
        "eval_drop": -106.4800033569336,
        "expert": "Imbalance",
        "fen": "1k5r/pp2pp2/2p5/2P5/P2P3r/1PN2Pp1/6P1/R2Q1RK1 w - - 0 30",
    },
    {
        "game": "tier_sf2200_1793046065_g6638",
        "opening": "French Defense",
        "move_num": 19,
        "blunder_move": "g5",
        "eval_drop": -106.33999633789062,
        "expert": "Imbalance",
        "fen": "r4rk1/2pb1p2/4pQpB/p3P2P/1qn5/8/5PPK/4R3 b - - 2 27",
    },
    {
        "game": "tier_sf1800_1793043025_g2517",
        "opening": "French Defense",
        "move_num": 28,
        "blunder_move": "Kc1",
        "eval_drop": -106.30999755859375,
        "expert": "Imbalance",
        "fen": "k6r/7p/p2Qp1pB/3pP1P1/1n1P4/1pP2N1R/q4P2/1K5R w - - 1 36",
    },
    {
        "game": "tier_sf2200_1793046065_g5555",
        "opening": "Sicilian Defense: Najdorf",
        "move_num": 68,
        "blunder_move": "Kg8",
        "eval_drop": -106.0,
        "expert": "Endgame",
        "fen": "5k2/4r3/5K2/8/1Q2P3/8/8/8 b - - 4 76",
    },
    {
        "game": "tier_sf2000_1793044475_g419",
        "opening": "Open Game (1.e4 e5)",
        "move_num": 19,
        "blunder_move": "Qg1+",
        "eval_drop": -105.25,
        "expert": "Imbalance",
        "fen": "r4rk1/5p1p/1qp1bPpQ/p7/4p3/6P1/PP4BP/3q1R1K b - - 0 27",
    },
    {
        "game": "tier_sf1600_1793041700_g2038",
        "opening": "Sicilian Defense",
        "move_num": 27,
        "blunder_move": "Kh8",
        "eval_drop": -105.06999969482422,
        "expert": "Imbalance",
        "fen": "4r1k1/p6P/3pN3/3P2Q1/q1p1p3/P1r2P2/1n4P1/6K1 b - - 0 35",
    },
    {
        "game": "tier_sf1600_1793041700_g1775",
        "opening": "Flank / Uncommon Opening",
        "move_num": 14,
        "blunder_move": "g4",
        "eval_drop": -104.81999969482422,
        "expert": "Imbalance",
        "fen": "rnbq1rk1/1p3p2/2n1p1P1/Pp1pP1p1/1b3P2/7Q/1B2B1P1/R4K1R b - - 0 22",
    },
    {
        "game": "tier_sf1600_1793041700_g159",
        "opening": "Scotch Game / Center Game",
        "move_num": 12,
        "blunder_move": "Rh1+",
        "eval_drop": -104.44999694824219,
        "expert": "Queenless",
        "fen": "r1bR1nk1/1p3pPp/2p3pB/p7/7r/5P2/1PP3P1/2K5 b - - 0 20",
    },
    {
        "game": "tier_sf2400_1793047759_g7517",
        "opening": "Flank / Uncommon Opening",
        "move_num": 11,
        "blunder_move": "Nxh5",
        "eval_drop": -104.04000091552734,
        "expert": "Imbalance",
        "fen": "r1b2rBk/pp2qpp1/1b6/7R/5P1P/2N1p1n1/PPQ3P1/4K1N1 b - - 3 19",
    },
    {
        "game": "tier_sf2000_1793044475_g3495",
        "opening": "Dutch Defense: Leningrad",
        "move_num": 117,
        "blunder_move": "Bc1",
        "eval_drop": -104.0,
        "expert": "Endgame",
        "fen": "B7/8/8/7R/5b2/1K6/8/k7 b - - 40 125",
    },
    {
        "game": "tier_sf2000_1793044475_g4165",
        "opening": "Sicilian Defense: Dragon",
        "move_num": 143,
        "blunder_move": "Nc6",
        "eval_drop": -104.0,
        "expert": "Endgame",
        "fen": "1n5k/8/6KN/8/8/B7/8/8 b - - 27 151",
    },
    {
        "game": "tier_sf1800_1793043025_g6548",
        "opening": "Sicilian Defense: Dragon",
        "move_num": 20,
        "blunder_move": "Bd4+",
        "eval_drop": -103.58000183105469,
        "expert": "Imbalance",
        "fen": "r4rk1/pp1q1p2/2np1B1Q/4p3/1PP5/6PP/P5P1/b5K1 b - - 12 28",
    },
    {
        "game": "tier_sf2400_1793047759_g2714",
        "opening": "Queen's Pawn Game",
        "move_num": 48,
        "blunder_move": "b3",
        "eval_drop": -103.38999938964844,
        "expert": "Endgame",
        "fen": "8/7r/6p1/6Bk/1p3P2/5K1b/1b6/7R b - - 1 56",
    },
    {
        "game": "tier_sf2200_1793046065_g2852",
        "opening": "Queen's Pawn Game",
        "move_num": 19,
        "blunder_move": "dxe6",
        "eval_drop": -103.36000061035156,
        "expert": "Open",
        "fen": "5r1k/rBp2pp1/P2pp2p/1P1P4/4Pn2/5P1q/2Q2P1P/R1R3K1 w - - 0 27",
    },
    {
        "game": "tier_sf1800_1793043025_g9643",
        "opening": "Sicilian Defense: Scheveningen",
        "move_num": 23,
        "blunder_move": "Kg6",
        "eval_drop": -103.08000183105469,
        "expert": "Imbalance",
        "fen": "5rk1/8/2qPp2p/p5pK/P1P5/B2B3r/3Q1P1P/1R3R2 w - - 3 31",
    },
    {
        "game": "tier_sf2000_1793044475_g8928",
        "opening": "English Opening",
        "move_num": 17,
        "blunder_move": "Qh6+",
        "eval_drop": -103.02999877929688,
        "expert": "Imbalance",
        "fen": "1r6/p1N2p1k/2p3p1/8/2P5/5qPb/PP3P2/R1Q1R1K1 w - - 0 25",
    },
    {
        "game": "tier_sf2400_1793047759_g4623",
        "opening": "Flank / Uncommon Opening",
        "move_num": 26,
        "blunder_move": "axb3",
        "eval_drop": -102.94000244140625,
        "expert": "Queenless",
        "fen": "2k5/1p3p2/p2R1Np1/4rb2/2P4B/1n3B1P/PP4P1/K7 w - - 1 34",
    },
    {
        "game": "tier_sf1600_1793041700_g9775",
        "opening": "Queen's Pawn Game",
        "move_num": 19,
        "blunder_move": "fxg6",
        "eval_drop": -102.7699966430664,
        "expert": "Imbalance",
        "fen": "r4rk1/pp2bp2/6R1/2n1pN1Q/2P1q2P/2P4P/5P2/5K2 b - - 0 27",
    },
    {
        "game": "tier_sf2200_1793046065_g6706",
        "opening": "Flank / Uncommon Opening",
        "move_num": 17,
        "blunder_move": "Bxb3",
        "eval_drop": -102.73999786376953,
        "expert": "Imbalance",
        "fen": "3rqrk1/2p2p1p/1pn1pP1Q/8/n1b5/1R3NPP/5PBK/5R2 b - - 0 25",
    },
    {
        "game": "tier_sf2400_1793047759_g4305",
        "opening": "Scotch Game / Center Game",
        "move_num": 43,
        "blunder_move": "bxc6",
        "eval_drop": -102.7300033569336,
        "expert": "Queenless",
        "fen": "8/1R5N/B1p3p1/PPP5/5nP1/6b1/5k2/2r3NK w - - 10 51",
    },
    {
        "game": "tier_sf2200_1793046065_g2375",
        "opening": "Queen's Indian Defense",
        "move_num": 15,
        "blunder_move": "Rxb7",
        "eval_drop": -102.69999694824219,
        "expert": "Imbalance",
        "fen": "r2r2k1/1p3ppp/3q4/3P4/2P3n1/2N1Pp2/P2Q1P1P/1RB2R1K w - - 2 23",
    },
    {
        "game": "tier_sf2400_1793047759_g4593",
        "opening": "Flank / Uncommon Opening",
        "move_num": 21,
        "blunder_move": "cxd4",
        "eval_drop": -102.66999816894531,
        "expert": "Imbalance",
        "fen": "r4rk1/qb3ppp/5B2/ppp2N1P/Pn1R2Q1/1p4P1/4nPBK/8 b - - 1 29",
    },
    {
        "game": "tier_sf2400_1793047759_g9517",
        "opening": "Sicilian Defense: Najdorf",
        "move_num": 30,
        "blunder_move": "Kg7",
        "eval_drop": -102.5999984741211,
        "expert": "Imbalance",
        "fen": "4Q2k/1q3p1p/5Np1/r5P1/p2p3P/1n4P1/2r5/3R2K1 b - - 1 38",
    },
    {
        "game": "tier_sf2400_1793047759_g4395",
        "opening": "Scotch Game / Center Game",
        "move_num": 20,
        "blunder_move": "Ka3",
        "eval_drop": -102.58000183105469,
        "expert": "Imbalance",
        "fen": "6k1/p4p1p/8/1pp2PPQ/2q2P2/1K5P/PPPB4/6R1 w - - 2 28",
    },
    {
        "game": "tier_sf2000_1793044475_g4469",
        "opening": "French Defense",
        "move_num": 32,
        "blunder_move": "Ka4",
        "eval_drop": -102.47000122070312,
        "expert": "Endgame",
        "fen": "3B2QR/1k6/p1p1P3/3p4/1K3Pp1/4q3/P1n5/8 w - - 5 40",
    },
]

def main():
    print("=" * 70)
    print("    RUNNING ANAK ALPHA-BETA SEARCH REGRESSION VERIFICATION")
    print("=" * 70)
    for i, pos in enumerate(CRITICAL_POSITIONS, 1):
        print(f"\n[{i}/{len(CRITICAL_POSITIONS)}] Testing {pos['opening']} (Move {pos['move_num']})")
        print(f"    FEN: {pos['fen']}")
        print(f"    Blunder in Match: {pos['blunder_move']} (Eval Drop: {pos['eval_drop']} cp)")
        if TEST_FEN_SCRIPT.exists():
            cmd = [sys.executable, str(TEST_FEN_SCRIPT), "--fen", pos["fen"], "--depth", "12"]
            subprocess.run(cmd)

if __name__ == "__main__":
    main()
