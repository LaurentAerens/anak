"""
Chess Position Query & Recipe Engine for Anak NNUE Training.
Provides flexible, query-based filtering and metric specification
(e.g., "pieces <= 16 and not has_queens", "openness >= 0.55")
and recipe quotas ("how much of what") across multiple data sources.
"""

import ast
import operator
from typing import Dict, Any, Optional, List, Callable, Union
import chess

# Allowed operators in safe AST query evaluation
SAFE_OPERATORS = {
    ast.Add: operator.add,
    ast.Sub: operator.sub,
    ast.Mult: operator.mul,
    ast.Div: operator.truediv,
    ast.FloorDiv: operator.floordiv,
    ast.Mod: operator.mod,
    ast.Pow: operator.pow,
    ast.USub: operator.neg,
    ast.UAdd: operator.pos,
    ast.Not: operator.not_,
    ast.Eq: operator.eq,
    ast.NotEq: operator.ne,
    ast.Lt: operator.lt,
    ast.LtE: operator.le,
    ast.Gt: operator.gt,
    ast.GtE: operator.ge,
    ast.In: lambda a, b: a in b,
    ast.NotIn: lambda a, b: a not in b,
}


def calculate_pawn_openness(board: chess.Board) -> float:
    """Computes pawn topology openness score matching Anak's C# PawnTopology.CalculateOpenness."""
    white_pawns = board.pieces_mask(chess.PAWN, chess.WHITE)
    black_pawns = board.pieces_mask(chess.PAWN, chess.BLACK)
    all_pawns = white_pawns | black_pawns
    total_pawns = bin(all_pawns).count("1")

    # 1. Locked pawns (rams): white pawn blocked directly ahead by black pawn
    locked_rams = white_pawns & (black_pawns >> 8)
    locked_count = bin(locked_rams).count("1")

    # 2. Open and semi-open files
    open_files = 0
    semi_open_files = 0
    file_masks = [0x0101010101010101 << i for i in range(8)]

    for mask in file_masks:
        has_white = (white_pawns & mask) != 0
        has_black = (black_pawns & mask) != 0
        if not has_white and not has_black:
            open_files += 1
        elif not has_white or not has_black:
            semi_open_files += 1

    # 3. Pawn tension (pawns in mutual attack contact)
    file_a = file_masks[0]
    file_h = file_masks[7]
    white_attacks = ((white_pawns << 7) & ~file_h) | ((white_pawns << 9) & ~file_a)
    tension_count = bin(white_attacks & black_pawns).count("1")

    # 4. Center pawns (files c, d, e, f)
    center_mask = file_masks[2] | file_masks[3] | file_masks[4] | file_masks[5]
    center_pawns = bin(all_pawns & center_mask).count("1")

    openness = (
        0.35
        + (open_files * 0.12)
        + (semi_open_files * 0.05)
        - (locked_count * 0.10)
        + (tension_count * 0.04)
        + ((8 - center_pawns) * 0.03)
        + ((16 - total_pawns) * 0.02)
    )

    return max(0.0, min(1.0, openness))


def extract_metrics_from_board(
    board: chess.Board,
    eval_val: Optional[float] = None,
    eval_delta: Optional[float] = None,
    is_blunder: bool = False,
    source: str = "general"
) -> Dict[str, Any]:
    """
    Extracts all queryable metrics from a chess.Board and evaluation metadata.
    """
    total_pieces = bin(board.occupied).count("1")
    white_pieces = bin(board.occupied_co[chess.WHITE]).count("1")
    black_pieces = bin(board.occupied_co[chess.BLACK]).count("1")

    white_queens = bin(board.pieces_mask(chess.QUEEN, chess.WHITE)).count("1")
    black_queens = bin(board.pieces_mask(chess.QUEEN, chess.BLACK)).count("1")
    total_queens = white_queens + black_queens

    white_rooks = bin(board.pieces_mask(chess.ROOK, chess.WHITE)).count("1")
    black_rooks = bin(board.pieces_mask(chess.ROOK, chess.BLACK)).count("1")
    total_rooks = white_rooks + black_rooks

    white_bishops = bin(board.pieces_mask(chess.BISHOP, chess.WHITE)).count("1")
    black_bishops = bin(board.pieces_mask(chess.BISHOP, chess.BLACK)).count("1")
    total_bishops = white_bishops + black_bishops

    white_knights = bin(board.pieces_mask(chess.KNIGHT, chess.WHITE)).count("1")
    black_knights = bin(board.pieces_mask(chess.KNIGHT, chess.BLACK)).count("1")
    total_knights = white_knights + black_knights

    white_pawns = bin(board.pieces_mask(chess.PAWN, chess.WHITE)).count("1")
    black_pawns = bin(board.pieces_mask(chess.PAWN, chess.BLACK)).count("1")
    total_pawns = white_pawns + black_pawns

    # Material non-king point values for imbalance calculation
    w_mat = (white_queens * 9) + (white_rooks * 5) + (white_bishops * 3) + (white_knights * 3) + white_pawns
    b_mat = (black_queens * 9) + (black_rooks * 5) + (black_bishops * 3) + (black_knights * 3) + black_pawns
    material_imbalance = abs(w_mat - b_mat)

    openness = calculate_pawn_openness(board)
    eval_float = float(eval_val) if eval_val is not None else 0.0
    delta_float = float(eval_delta) if eval_delta is not None else 0.0

    blunder_flag = is_blunder or (delta_float >= 1.5) or (abs(eval_float) >= 2.5)

    return {
        # Piece counts
        "pieces": total_pieces,
        "piece_count": total_pieces,
        "white_pieces": white_pieces,
        "black_pieces": black_pieces,
        # Queens
        "queens": total_queens,
        "queens_count": total_queens,
        "white_queens": white_queens,
        "black_queens": black_queens,
        "has_queens": total_queens > 0,
        "has_queen": total_queens > 0,
        "no_queens": total_queens == 0,
        "no_queen": total_queens == 0,
        # Other pieces
        "rooks": total_rooks,
        "has_rooks": total_rooks > 0,
        "bishops": total_bishops,
        "has_bishops": total_bishops > 0,
        "knights": total_knights,
        "has_knights": total_knights > 0,
        "pawns": total_pawns,
        "has_pawns": total_pawns > 0,
        "pawn_count": total_pawns,
        # Material
        "material_imbalance": material_imbalance,
        "is_endgame": total_pieces <= 16,
        "is_deep_endgame": total_pieces <= 10,
        # Topology
        "openness": openness,
        "is_open": openness >= 0.50,
        "is_closed": openness < 0.50,
        # Evals & Blunders
        "eval": eval_float,
        "abs_eval": abs(eval_float),
        "eval_delta": delta_float,
        "is_blunder": blunder_flag,
        # Turn & Checks
        "turn": "w" if board.turn == chess.WHITE else "b",
        "in_check": board.is_check(),
        "source": source.lower(),
    }


class SafeQueryEvaluator:
    """Safely evaluates boolean query expressions using a restricted Python AST."""
    def __init__(self, query_str: str):
        self.query_str = query_str.strip()
        if not self.query_str:
            self.ast_tree = None
        else:
            self.ast_tree = ast.parse(self.query_str, mode="eval")

    def evaluate(self, metrics: Dict[str, Any]) -> bool:
        if self.ast_tree is None:
            return True
        return bool(self._eval_node(self.ast_tree.body, metrics))

    def _eval_node(self, node: ast.AST, metrics: Dict[str, Any]) -> Any:
        if isinstance(node, ast.Constant):
            return node.value
        elif isinstance(node, ast.Name):
            name = node.id.lower()
            if name in ("true", "t"):
                return True
            if name in ("false", "f"):
                return False
            if name in ("none", "null"):
                return None
            if name in metrics:
                return metrics[name]
            # If not in metrics, return None or False
            return None
        elif isinstance(node, ast.UnaryOp):
            op = SAFE_OPERATORS.get(type(node.op))
            if not op:
                raise ValueError(f"Unsupported unary operator: {type(node.op)}")
            operand = self._eval_node(node.operand, metrics)
            return op(operand)
        elif isinstance(node, ast.BinOp):
            op = SAFE_OPERATORS.get(type(node.op))
            if not op:
                raise ValueError(f"Unsupported binary operator: {type(node.op)}")
            left = self._eval_node(node.left, metrics)
            right = self._eval_node(node.right, metrics)
            return op(left, right)
        elif isinstance(node, ast.BoolOp):
            if isinstance(node.op, ast.And):
                for val in node.values:
                    if not self._eval_node(val, metrics):
                        return False
                return True
            elif isinstance(node.op, ast.Or):
                for val in node.values:
                    if self._eval_node(val, metrics):
                        return True
                return False
        elif isinstance(node, ast.Compare):
            left = self._eval_node(node.left, metrics)
            for op_type, comparator in zip(node.ops, node.comparators):
                op = SAFE_OPERATORS.get(type(op_type))
                if not op:
                    raise ValueError(f"Unsupported comparison operator: {type(op_type)}")
                right = self._eval_node(comparator, metrics)
                if left is None or right is None:
                    return False
                if not op(left, right):
                    return False
                left = right
            return True
        elif isinstance(node, ast.Call):
            # Safe functions: abs(), round(), min(), max()
            func_name = getattr(node.func, "id", "").lower()
            args = [self._eval_node(a, metrics) for a in node.args]
            if func_name == "abs":
                return abs(args[0])
            elif func_name == "round":
                return round(*args)
            elif func_name == "min":
                return min(*args)
            elif func_name == "max":
                return max(*args)
            raise ValueError(f"Disallowed function call in query: '{func_name}'")

        raise ValueError(f"Unsupported AST node in query: {type(node)}")


class PositionQuery:
    """Wraps a query string with compilation and validation."""
    def __init__(self, query_string: Optional[str] = None):
        self.query_string = query_string or ""
        self._evaluator = SafeQueryEvaluator(self.query_string) if self.query_string else None

    def matches(self, metrics: Dict[str, Any]) -> bool:
        if not self._evaluator:
            return True
        try:
            return self._evaluator.evaluate(metrics)
        except Exception:
            return False

    def matches_board(
        self,
        board: chess.Board,
        eval_val: Optional[float] = None,
        eval_delta: Optional[float] = None,
        is_blunder: bool = False,
        source: str = "general"
    ) -> bool:
        metrics = extract_metrics_from_board(board, eval_val, eval_delta, is_blunder, source)
        return self.matches(metrics)


class DatasetRecipe:
    """
    Defines a multi-target dataset recipe with query metrics and target quotas ('how much of what').
    Example:
      recipe = DatasetRecipe({
          "open_middlegame": {"query": "openness >= 0.50 and pieces > 16", "ratio": 0.40},
          "queenless_endgame": {"query": "no_queens and pieces <= 16", "ratio": 0.25},
          "tactical_blunders": {"query": "is_blunder or abs_eval >= 2.0", "ratio": 0.20},
          "closed_chains": {"query": "openness < 0.45", "ratio": 0.15}
      })
    """
    def __init__(self, recipe_dict: Dict[str, Dict[str, Any]]):
        self.slices = []
        total_ratio = 0.0

        for name, spec in recipe_dict.items():
            query_str = spec.get("query", "")
            ratio = float(spec.get("ratio", 1.0))
            target_model = spec.get("target_model", "open")
            total_ratio += ratio
            self.slices.append({
                "name": name,
                "query": PositionQuery(query_str),
                "ratio": ratio,
                "target_model": target_model,
                "count": 0,
            })

        # Normalize ratios to 1.0
        if total_ratio > 0:
            for s in self.slices:
                s["normalized_ratio"] = s["ratio"] / total_ratio
        else:
            for s in self.slices:
                s["normalized_ratio"] = 1.0 / len(self.slices)

    def route_sample(self, metrics: Dict[str, Any]) -> Optional[str]:
        """
        Determines which recipe slice should claim this sample based on query matches and quota ratios.
        Returns the slice name or None if rejected.
        """
        matching_slices = [s for s in self.slices if s["query"].matches(metrics)]
        if not matching_slices:
            return None

        # Prioritize the matching slice that is currently furthest below its target ratio
        total_collected = sum(s["count"] for s in self.slices)
        if total_collected == 0:
            chosen = matching_slices[0]
        else:
            # Under-representation score = target_ratio - (current_count / total_collected)
            chosen = max(
                matching_slices,
                key=lambda s: s["normalized_ratio"] - (s["count"] / total_collected)
            )

        chosen["count"] += 1
        return chosen["name"]

    def summary(self) -> Dict[str, Any]:
        total = sum(s["count"] for s in self.slices)
        return {
            s["name"]: {
                "count": s["count"],
                "target_ratio": s["normalized_ratio"],
                "achieved_ratio": (s["count"] / max(1, total)),
            }
            for s in self.slices
        }
