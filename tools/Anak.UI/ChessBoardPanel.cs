#if WINDOWS
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Anak.Core;
using ChessColor = Anak.Core.Color;
using ChessMove = Anak.Core.Move;
using DrawingColor = System.Drawing.Color;

namespace Anak.UI;

/// <summary>
/// Interactive, double-buffered chessboard control for local debugging.
/// Credit & Attribution: Inspired by open-source C# chess visualizer designs (Sebastian Lague Chess-Coding-Adventure & WinForms Chess UI, MIT License).
/// </summary>
public sealed class ChessBoardPanel : Control
{
    private Board _board;
    private bool _flipped;
    private Square _selectedSquare = Square.None;
    private readonly List<ChessMove> _legalMovesFromSelected = [];
    private ChessMove _lastMove = ChessMove.Null;

    private static readonly DrawingColor _lightSquareColor = DrawingColor.FromArgb(240, 217, 181);
    private static readonly DrawingColor _darkSquareColor = DrawingColor.FromArgb(181, 136, 99);
    private static readonly DrawingColor _selectedColor = DrawingColor.FromArgb(160, 130, 151, 105);
    private static readonly DrawingColor _lastMoveColor = DrawingColor.FromArgb(130, 205, 210, 106);
    private static readonly DrawingColor _checkColor = DrawingColor.FromArgb(180, 235, 75, 75);
    private static readonly DrawingColor _legalMoveDotColor = DrawingColor.FromArgb(100, 50, 50, 50);
    private static readonly DrawingColor _legalCaptureRingColor = DrawingColor.FromArgb(140, 180, 30, 30);

    public event Action<ChessMove>? MoveMade;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Board Board
    {
        get => _board;
        set
        {
            _board = value;
            _selectedSquare = Square.None;
            _legalMovesFromSelected.Clear();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Flipped
    {
        get => _flipped;
        set
        {
            _flipped = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ChessMove LastMove
    {
        get => _lastMove;
        set
        {
            _lastMove = value;
            Invalidate();
        }
    }

    public ChessBoardPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _board = Fen.Parse(Fen.StartPosition);
        Size = new Size(560, 560);
    }

    private int SquareSize => Math.Min(Width, Height) / 8;

    private int BoardOffsetX => (Width - (SquareSize * 8)) / 2;
    private int BoardOffsetY => (Height - (SquareSize * 8)) / 2;

    private Point SquareToCoordinates(Square sq)
    {
        int file = (int)sq.File();
        int rank = (int)sq.Rank();

        int drawFile = _flipped ? 7 - file : file;
        int drawRank = _flipped ? rank : 7 - rank;

        return new Point(BoardOffsetX + (drawFile * SquareSize), BoardOffsetY + (drawRank * SquareSize));
    }

    private Square CoordinatesToSquare(int x, int y)
    {
        int relX = x - BoardOffsetX;
        int relY = y - BoardOffsetY;

        if (relX < 0 || relY < 0) return Square.None;

        int drawFile = relX / SquareSize;
        int drawRank = relY / SquareSize;

        if (drawFile is < 0 or > 7 || drawRank is < 0 or > 7) return Square.None;

        int file = _flipped ? 7 - drawFile : drawFile;
        int rank = _flipped ? drawRank : 7 - drawRank;

        return SquareExtensions.Create(file, rank);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left) return;

        Square clickedSquare = CoordinatesToSquare(e.X, e.Y);
        if (clickedSquare == Square.None) return;

        // If a piece was already selected, check if clicked square is a legal move destination
        if (_selectedSquare != Square.None)
        {
            ChessMove? matchedMove = null;
            foreach (var m in _legalMovesFromSelected)
            {
                if (m.To == clickedSquare)
                {
                    // If promotion, default to Queen promotion
                    if (m.IsPromotion)
                    {
                        if (m.PromotionPieceType == PieceType.Queen)
                        {
                            matchedMove = m;
                            break;
                        }
                    }
                    else
                    {
                        matchedMove = m;
                        break;
                    }
                }
            }

            if (matchedMove.HasValue)
            {
                _selectedSquare = Square.None;
                _legalMovesFromSelected.Clear();
                _lastMove = matchedMove.Value;
                MoveMade?.Invoke(matchedMove.Value);
                Invalidate();
                return;
            }
        }

        // Otherwise select piece on square if it matches side to move
        Piece piece = _board.GetPieceAt(clickedSquare);
        if (piece != Piece.None && piece.Color() == _board.SideToMove)
        {
            _selectedSquare = clickedSquare;
            _legalMovesFromSelected.Clear();

            MoveList allMoves = default;
            _board.GenerateLegalMoves(ref allMoves);

            for (int i = 0; i < allMoves.Count; i++)
            {
                if (allMoves[i].From == clickedSquare)
                {
                    _legalMovesFromSelected.Add(allMoves[i]);
                }
            }
        }
        else
        {
            _selectedSquare = Square.None;
            _legalMovesFromSelected.Clear();
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int sqSize = SquareSize;

        // 1. Draw Squares
        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                Square sq = SquareExtensions.Create(file, rank);
                Point pt = SquareToCoordinates(sq);
                Rectangle rect = new(pt.X, pt.Y, sqSize, sqSize);

                bool isLight = ((file + rank) % 2) != 0;
                DrawingColor squareColor = isLight ? _lightSquareColor : _darkSquareColor;

                // Last move highlights
                if (!_lastMove.IsNull && (sq == _lastMove.From || sq == _lastMove.To))
                {
                    squareColor = BlendColors(squareColor, _lastMoveColor);
                }

                // Selected square highlight
                if (sq == _selectedSquare)
                {
                    squareColor = BlendColors(squareColor, _selectedColor);
                }

                // Check highlight on king
                if (_board.IsInCheck() && _board.GetPieceAt(sq) == PieceExtensions.Create(_board.SideToMove, PieceType.King))
                {
                    squareColor = BlendColors(squareColor, _checkColor);
                }

                using var brush = new SolidBrush(squareColor);
                g.FillRectangle(brush, rect);

                // File/Rank notation coordinates on edges
                DrawCoordinates(g, file, rank, pt, sqSize, isLight);
            }
        }

        // 2. Draw Legal Move Markers
        foreach (var move in _legalMovesFromSelected)
        {
            Point pt = SquareToCoordinates(move.To);
            Rectangle rect = new(pt.X, pt.Y, sqSize, sqSize);

            Piece targetPiece = _board.GetPieceAt(move.To);
            if (targetPiece != Piece.None || move.IsEnPassant)
            {
                // Capture ring
                using var pen = new Pen(_legalCaptureRingColor, sqSize * 0.10f);
                int inset = (int)(sqSize * 0.12f);
                g.DrawEllipse(pen, rect.X + inset, rect.Y + inset, rect.Width - (2 * inset), rect.Height - (2 * inset));
            }
            else
            {
                // Move dot
                int dotRadius = sqSize / 6;
                using var dotBrush = new SolidBrush(_legalMoveDotColor);
                g.FillEllipse(dotBrush, rect.X + (sqSize / 2) - dotRadius, rect.Y + (sqSize / 2) - dotRadius, dotRadius * 2, dotRadius * 2);
            }
        }

        // 3. Draw Pieces
        using Font pieceFont = new("Segoe UI Symbol", sqSize * 0.65f, FontStyle.Regular, GraphicsUnit.Pixel);
        using StringFormat sf = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        for (int sqIdx = 0; sqIdx < 64; sqIdx++)
        {
            Square sq = (Square)sqIdx;
            Piece piece = _board.GetPieceAt(sq);
            if (piece == Piece.None) continue;

            Point pt = SquareToCoordinates(sq);
            RectangleF rect = new(pt.X, pt.Y, sqSize, sqSize);

            char symbol = PieceToUnicode(piece);
            bool isWhitePiece = piece.Color() == ChessColor.White;

            // Draw crisp piece shadow/outline for readability on all board colors
            using var shadowBrush = new SolidBrush(isWhitePiece ? DrawingColor.FromArgb(140, 20, 20, 20) : DrawingColor.FromArgb(180, 240, 240, 240));
            RectangleF shadowRect = new(rect.X + (isWhitePiece ? 1 : 0), rect.Y + (isWhitePiece ? 1 : 0), rect.Width, rect.Height);
            g.DrawString(symbol.ToString(), pieceFont, shadowBrush, shadowRect, sf);

            using var pieceBrush = new SolidBrush(isWhitePiece ? DrawingColor.White : DrawingColor.FromArgb(20, 20, 20));
            g.DrawString(symbol.ToString(), pieceFont, pieceBrush, rect, sf);
        }
    }

    private void DrawCoordinates(Graphics g, int file, int rank, Point pt, int sqSize, bool isLight)
    {
        using Font coordFont = new("Segoe UI", sqSize * 0.16f, FontStyle.Bold, GraphicsUnit.Pixel);
        using Brush coordBrush = new SolidBrush(isLight ? _darkSquareColor : _lightSquareColor);

        // File label (bottom-right of rank 0 / drawRank 7)
        int drawRank = _flipped ? rank : 7 - rank;
        if (drawRank == 7)
        {
            char fileChar = (char)('a' + file);
            g.DrawString(fileChar.ToString(), coordFont, coordBrush, pt.X + sqSize - (sqSize * 0.22f), pt.Y + sqSize - (sqSize * 0.25f));
        }

        // Rank label (top-left of file 0 / drawFile 0)
        int drawFile = _flipped ? 7 - file : file;
        if (drawFile == 0)
        {
            char rankChar = (char)('1' + rank);
            g.DrawString(rankChar.ToString(), coordFont, coordBrush, pt.X + (sqSize * 0.05f), pt.Y + (sqSize * 0.05f));
        }
    }

    private static char PieceToUnicode(Piece piece) => piece switch
    {
        Piece.WhiteKing => '♔',
        Piece.WhiteQueen => '♕',
        Piece.WhiteRook => '♖',
        Piece.WhiteBishop => '♗',
        Piece.WhiteKnight => '♘',
        Piece.WhitePawn => '♙',
        Piece.BlackKing => '♚',
        Piece.BlackQueen => '♛',
        Piece.BlackRook => '♜',
        Piece.BlackBishop => '♝',
        Piece.BlackKnight => '♞',
        Piece.BlackPawn => '♟',
        _ => ' '
    };

    private static DrawingColor BlendColors(DrawingColor bg, DrawingColor blend)
    {
        float alpha = blend.A / 255.0f;
        int r = (int)((blend.R * alpha) + (bg.R * (1 - alpha)));
        int g = (int)((blend.G * alpha) + (bg.G * (1 - alpha)));
        int b = (int)((blend.B * alpha) + (bg.B * (1 - alpha)));
        return DrawingColor.FromArgb(255, r, g, b);
    }
}
#endif
