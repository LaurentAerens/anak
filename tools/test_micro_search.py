import sys
sys.path.insert(0, '.')
import chess
import struct, numpy as np

# Load micro weights
with open('src/Anak.Protocol/bin/Release/net11.0/networks/micro.nnue', 'rb') as f:
    hdr = f.read(16)
    payload = f.read()

dim = 16
buckets = 16
ft_len = buckets * 768 * dim
ft_w = np.frombuffer(payload[:ft_len], dtype=np.int8).reshape(buckets*768, dim)
ft_b = np.frombuffer(payload[ft_len:ft_len+32], dtype=np.int16)
out_w = np.frombuffer(payload[ft_len+32:ft_len+32+64], dtype=np.int16)
out_b = struct.unpack('<i', payload[ft_len+32+64:ft_len+32+68])[0]

from training.dataset import encode_board_features
from training.model import get_king_bucket

def eval_micro(b):
    our_idx, their_idx = encode_board_features(b)
    w_k = next((x - 320 for x in our_idx if 320 <= x < 384), 4)
    b_k = next((x - 320 for x in their_idx if 320 <= x < 384), 4)
    w_b = get_king_bucket(w_k)
    b_b = get_king_bucket(b_k)
    
    our_acc = ft_b.astype(np.int32).copy()
    for idx in our_idx: our_acc += ft_w[w_b*768 + idx].astype(np.int32)
    their_acc = ft_b.astype(np.int32).copy()
    for idx in their_idx: their_acc += ft_w[b_b*768 + idx].astype(np.int32)
    
    our_cl = np.clip(our_acc, 0, 64)
    their_cl = np.clip(their_acc, 0, 64)
    comb = np.concatenate([our_cl, their_cl])
    res = int(out_b) + int(np.dot(comb, out_w.astype(np.int32)))
    return int((res * 100) // (64 * 64))

def micro_search(b, depth, alpha, beta):
    if depth == 0 or b.is_game_over():
        return eval_micro(b)
    for m in b.legal_moves:
        b.push(m)
        score = -micro_search(b, depth - 1, -beta, -alpha)
        b.pop()
        if score >= beta:
            return beta
        if score > alpha:
            alpha = score
    return alpha

fen = '5nk1/3qb3/Q3p1p1/3pPrBp/1P1P4/6P1/5pPP/2R2K2 w - - 2 32'
b = chess.Board(fen)

print("Root Position FEN:", fen)
for m in [chess.Move.from_uci('g5e7'), chess.Move.from_uci('g5d2')]:
    b.push(m)
    # Opponent searches response with micro
    score_after = -micro_search(b, depth=4, alpha=-10000, beta=10000)
    print(f"Move {m.uci()}: Micro Depth 4 Score for White = {score_after:+d} cp")
    b.pop()
