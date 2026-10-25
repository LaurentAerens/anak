import chess, chess.pgn, re

with open('tools/fastchess/results/anak_vs_sf2000_20261025_141916.pgn') as f:
    for i in range(50):
        game = chess.pgn.read_game(f)
        if not game: break
        
        is_anak_white = 'Anak' in game.headers.get('White', '')
        is_anak_black = 'Anak' in game.headers.get('Black', '')
        res = game.headers.get('Result', '')
        anak_lost = (is_anak_white and res == '0-1') or (is_anak_black and res == '1-0')
        if not anak_lost: continue
        
        b = game.board()
        for node in game.mainline():
            m = node.move
            comm = node.comment
            is_anak = (is_anak_white and b.turn == chess.WHITE) or (is_anak_black and b.turn == chess.BLACK)
            match = re.search(r'([+-]?\d+\.\d+)/(\d+)', comm)
            if is_anak and match:
                ev = float(match.group(1))
                if ev >= 2.5:
                    print(f"Game {i}: Anak {game.headers.get('White')} vs {game.headers.get('Black')}")
                    print(f"Eval: +{ev:.2f}")
                    print(f"FEN where Anak thought it was +{ev:.2f}: {b.fen()}")
                    print(f"Anak played: {m.uci()}")
                    exit(0)
            b.push(m)
