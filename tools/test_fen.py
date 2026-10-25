import chess, chess.pgn, re

with open('tools/fastchess/results/anak_vs_sf2000_20261025_141916.pgn') as f:
    while True:
        game = chess.pgn.read_game(f)
        if not game: break
        
        is_anak_white = 'Anak' in game.headers.get('White', '')
        is_anak_black = 'Anak' in game.headers.get('Black', '')
        res = game.headers.get('Result', '')
        anak_lost = (is_anak_white and res == '0-1') or (is_anak_black and res == '1-0')
        if not anak_lost: continue
        
        max_ev = -9999
        b = game.board()
        for node in game.mainline():
            m = node.move
            comm = node.comment
            b.push(m)
            match = re.search(r'([+-]?\d+\.\d+)/(\d+)', comm)
            if match:
                ev = float(match.group(1))
                is_anak = (is_anak_white and b.turn == chess.BLACK) or (is_anak_black and b.turn == chess.WHITE)
                if is_anak and ev > max_ev:
                    max_ev = ev
                if max_ev >= 2.0 and is_anak and ev < -1.0:
                    # Found throw!
                    print(f"Game: {game.headers.get('White')} vs {game.headers.get('Black')}")
                    print(f"Max eval was: +{max_ev:.2f}")
                    print(f"Blunder move: {m.uci()} (eval: {ev:+.2f})")
                    # Pop move to get FEN right before blunder
                    b.pop()
                    print(f"FEN before blunder: {b.fen()}")
                    print(f"Side to move: {'White' if b.turn == chess.WHITE else 'Black'}")
                    exit(0)
