#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Anak.Core;
using Anak.Data.PolyGlot;
using Anak.Protocol.Uci;
using Anak.Search;
using ChessColor = Anak.Core.Color;
using ChessMove = Anak.Core.Move;
using DrawingColor = System.Drawing.Color;

namespace Anak.UI;

/// <summary>
/// Custom vertical evaluation bar visualizer (White vs Black win probability).
/// </summary>
public sealed class EvalBarControl : Control
{
    private double _whiteWinProb = 0.50; // 0.0 to 1.0

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double WhiteWinProbability
    {
        get => _whiteWinProb;
        set
        {
            _whiteWinProb = Math.Clamp(value, 0.0, 1.0);
            Invalidate();
        }
    }

    /// <summary>
    /// Updates the evaluation bar win probability from a centipawn score from White's perspective (or mate score).
    /// Standard logistic sigmoid: P(Win) = 1.0 / (1.0 + Math.Pow(10.0, -scoreInCp / 400.0)).
    /// </summary>
    /// <param name="scoreInCp">Centipawn score relative to White (positive favors White).</param>
    /// <param name="mateIn">Mate distance relative to White (positive White mates, negative Black mates, 0 if not mate).</param>
    public void SetScore(int scoreInCp, int mateIn = 0)
    {
        if (mateIn > 0)
        {
            WhiteWinProbability = 1.0;
        }
        else if (mateIn < 0)
        {
            WhiteWinProbability = 0.0;
        }
        else
        {
            WhiteWinProbability = CalculateWinProbability(scoreInCp);
        }
    }

    /// <summary>
    /// Calculates win probability using the standard logistic sigmoid:
    /// P(Win) = 1.0 / (1.0 + Math.Pow(10.0, -scoreInCp / 400.0)).
    /// </summary>
    public static double CalculateWinProbability(double scoreInCp)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, -scoreInCp / 400.0));
    }

    public EvalBarControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        Width = 24;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background: Black advantage (top)
        using var blackBrush = new SolidBrush(DrawingColor.FromArgb(50, 50, 50));
        g.FillRectangle(blackBrush, 0, 0, Width, Height);

        // White portion: White advantage (bottom)
        int whiteHeight = (int)(Height * _whiteWinProb);
        int whiteY = Height - whiteHeight;

        using var whiteBrush = new SolidBrush(DrawingColor.FromArgb(240, 240, 240));
        g.FillRectangle(whiteBrush, 0, whiteY, Width, whiteHeight);

        // Center line
        using var centerPen = new Pen(DrawingColor.FromArgb(160, 100, 100, 100), 1.5f);
        g.DrawLine(centerPen, 0, Height / 2, Width, Height / 2);

        // Border
        using var borderPen = new Pen(DrawingColor.FromArgb(120, 120, 120), 1);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// Primary debugging window providing interactive play, MCTS search monitoring, and FEN analysis.
/// Credit & Attribution: Inspired by open-source C# chess visualizer designs (Sebastian Lague Chess-Coding-Adventure & WinForms Chess UI, MIT License).
/// </summary>
public sealed class MainForm : Form
{
    private Board _board;
    private readonly ChessBoardPanel _boardPanel;
    private readonly EvalBarControl _evalBar;
    private readonly Label _statusLabel;
    private readonly Label _evalLabel;
    private readonly Label _searchStatsLabel;
    private readonly TextBox _pvTextBox;
    private readonly ListView _topMovesListView;
    private readonly ListBox _historyListBox;
    private readonly TextBox _fenTextBox;
    private readonly NumericUpDown _thinkTimeNumeric;
    private readonly NumericUpDown _moveRandomnessNumeric;
    private readonly CheckBox _engineBlackCheckBox;
    private readonly CheckBox _engineWhiteCheckBox;
    private readonly CheckBox _useBookCheckBox;
    private readonly CheckBox _openingVarietyCheckBox;
    private readonly ComboBox _timeControlCombo;
    private readonly Panel _whiteClockPanel;
    private readonly Panel _blackClockPanel;
    private readonly Label _whiteClockLabel;
    private readonly Label _blackClockLabel;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly System.Windows.Forms.Timer _selfPlayTimer;
    private readonly Stopwatch _turnStopwatch = new();

    private double _whiteTimeMs = 300_000;
    private double _blackTimeMs = 300_000;
    private double _incrementMs = 2_000;

    private readonly AlphaBetaEngine _alphaBetaEngine;
    private MCTSEngine? _mctsEngine;
    private ISearchEngine _engine;
    private readonly ComboBox _engineCombo;
    private readonly IEvaluator _evaluator;
    private readonly PolyGlotBook _book = new();
    private CancellationTokenSource? _searchCts;
    private bool _isSearching;

    public MainForm()
    {
        _board = Fen.Parse(Fen.StartPosition);
        _evaluator = UciEngineAdapter.CreateEvaluator();
        string evalTag = _evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnue && nnue.IsActive
            ? "[NNUE Active]"
            : "[Classical Fallback]";
        Text = $"Anak Chess Engine — Local Debugging & Visualizer {evalTag}";
        _alphaBetaEngine = new AlphaBetaEngine(_evaluator, defaultHashMb: 64);
        _engine = _alphaBetaEngine;
        _book.LoadOrDefault("book.bin");

        // Layout panels
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12)
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28)); // Eval bar
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); // Board
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); // Side controls

        // 1. Eval Bar
        _evalBar = new EvalBarControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 8, 8)
        };
        mainLayout.Controls.Add(_evalBar, 0, 0);

        // 2. Board Panel
        _boardPanel = new ChessBoardPanel
        {
            Dock = DockStyle.Fill,
            Board = _board
        };
        _boardPanel.MoveMade += OnPlayerMoveMade;
        mainLayout.Controls.Add(_boardPanel, 1, 0);

        // 3. Right Control / Telemetry Panel
        var rightPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8)
        };

        int y = 4;

        // Title & Credits
        var titleLabel = new Label
        {
            Text = "Anak Engine Diagnostics",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = DrawingColor.FromArgb(30, 30, 45),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(titleLabel);
        y += 26;

        var creditsLabel = new Label
        {
            Text = "Local visualizer using Anak (Alpha-Beta Minimax / MCTS)\nAdapted from open-source C# chess GUI designs (MIT License)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = DrawingColor.FromArgb(100, 100, 115),
            Location = new Point(4, y),
            Size = new Size(360, 32)
        };
        rightPanel.Controls.Add(creditsLabel);
        y += 36;

        // Digital Chess Clocks (White vs Black)
        var clockContainer = new TableLayoutPanel
        {
            Location = new Point(4, y),
            Size = new Size(360, 44),
            ColumnCount = 2,
            RowCount = 1
        };
        clockContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        clockContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _whiteClockPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DrawingColor.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 3, 0)
        };
        _whiteClockLabel = new Label
        {
            Text = "White  05:00",
            Font = new Font("Consolas", 12f, FontStyle.Bold),
            ForeColor = DrawingColor.FromArgb(20, 20, 30),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _whiteClockPanel.Controls.Add(_whiteClockLabel);
        clockContainer.Controls.Add(_whiteClockPanel, 0, 0);

        _blackClockPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = DrawingColor.FromArgb(45, 45, 55),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3, 0, 0, 0)
        };
        _blackClockLabel = new Label
        {
            Text = "Black  05:00",
            Font = new Font("Consolas", 12f, FontStyle.Bold),
            ForeColor = DrawingColor.FromArgb(240, 240, 245),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _blackClockPanel.Controls.Add(_blackClockLabel);
        clockContainer.Controls.Add(_blackClockPanel, 1, 0);

        rightPanel.Controls.Add(clockContainer);
        y += 48;

        // Status Label
        _statusLabel = new Label
        {
            Text = "White to move",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = DrawingColor.FromArgb(40, 80, 160),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(_statusLabel);
        y += 28;

        // Evaluation Label
        _evalLabel = new Label
        {
            Text = "Eval: 0.00 (White Win%: 50%)",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = DrawingColor.FromArgb(50, 50, 60),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(_evalLabel);
        y += 24;

        // Search Stats Label
        _searchStatsLabel = new Label
        {
            Text = "Engine: Alpha-Beta Minimax (Idle)",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = DrawingColor.FromArgb(70, 70, 80),
            Location = new Point(4, y),
            Size = new Size(360, 46)
        };
        rightPanel.Controls.Add(_searchStatsLabel);
        y += 50;

        // PV text box
        var pvTitle = new Label
        {
            Text = "Principal Variation (PV):",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(pvTitle);
        y += 20;

        _pvTextBox = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(4, y),
            Size = new Size(360, 44),
            Font = new Font("Consolas", 9f)
        };
        rightPanel.Controls.Add(_pvTextBox);
        y += 50;

        // Top 5 Candidate Moves Table
        var topMovesTitle = new Label
        {
            Text = "Top 5 Candidate Moves:",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(topMovesTitle);
        y += 20;

        _topMovesListView = new ListView
        {
            Location = new Point(4, y),
            Size = new Size(360, 112),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 8.5f)
        };
        _topMovesListView.Columns.Add("#", 24);
        _topMovesListView.Columns.Add("Move", 50);
        _topMovesListView.Columns.Add("Score", 82);
        _topMovesListView.Columns.Add("Win%", 46);
        _topMovesListView.Columns.Add("PV Line", 150);
        _topMovesListView.ItemSelectionChanged += OnTopMoveSelected;
        rightPanel.Controls.Add(_topMovesListView);
        y += 118;

        // Move History List
        var historyTitle = new Label
        {
            Text = "Move History:",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(4, y),
            AutoSize = true
        };
        rightPanel.Controls.Add(historyTitle);
        y += 20;

        _historyListBox = new ListBox
        {
            Location = new Point(4, y),
            Size = new Size(360, 90),
            Font = new Font("Consolas", 9.5f)
        };
        rightPanel.Controls.Add(_historyListBox);
        y += 98;

        // Engine Options: Engine, Time Control & checkboxes
        var optionsPanel = new FlowLayoutPanel
        {
            Location = new Point(4, y),
            Size = new Size(360, 116),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        optionsPanel.Controls.Add(new Label { Text = "Engine:", AutoSize = true, Margin = new Padding(0, 5, 2, 0) });
        _engineCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 145,
            Margin = new Padding(0, 2, 8, 0)
        };
        _engineCombo.Items.AddRange(["Alpha-Beta Minimax", "MCTS"]);
        _engineCombo.SelectedIndex = 0; // Default: Alpha-Beta Minimax
        _engineCombo.SelectedIndexChanged += (_, _) => OnEngineSelectionChanged();
        optionsPanel.Controls.Add(_engineCombo);

        optionsPanel.Controls.Add(new Label { Text = "Clock:", AutoSize = true, Margin = new Padding(0, 5, 2, 0) });
        _timeControlCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 125,
            Margin = new Padding(0, 2, 6, 0)
        };
        _timeControlCombo.Items.AddRange(["5m + 2s (Blitz)", "3m + 2s (Blitz)", "1m + 1s (Bullet)", "10m + 5s (Rapid)", "Fixed Time (ms)"]);
        _timeControlCombo.SelectedIndex = 0; // Default: 5 min + 2s Blitz
        _timeControlCombo.SelectedIndexChanged += (_, _) => ResetTimeControl();
        optionsPanel.Controls.Add(_timeControlCombo);

        optionsPanel.Controls.Add(new Label { Text = "Fixed (ms):", AutoSize = true, Margin = new Padding(0, 5, 2, 0) });
        _thinkTimeNumeric = new NumericUpDown
        {
            Minimum = 50,
            Maximum = 30000,
            Value = 500,
            Increment = 100,
            Width = 60,
            Enabled = false
        };
        optionsPanel.Controls.Add(_thinkTimeNumeric);

        _engineWhiteCheckBox = new CheckBox
        {
            Text = "White",
            AutoSize = true,
            Checked = false,
            Margin = new Padding(0, 4, 8, 0)
        };
        optionsPanel.Controls.Add(_engineWhiteCheckBox);

        _engineBlackCheckBox = new CheckBox
        {
            Text = "Black",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 4, 8, 0)
        };
        optionsPanel.Controls.Add(_engineBlackCheckBox);

        _useBookCheckBox = new CheckBox
        {
            Text = "Opening Book",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(0, 4, 8, 0)
        };
        optionsPanel.Controls.Add(_useBookCheckBox);

        _openingVarietyCheckBox = new CheckBox
        {
            Text = "Opening Variety",
            AutoSize = true,
            Checked = true,
            Enabled = false,
            Margin = new Padding(0, 4, 6, 0)
        };
        optionsPanel.Controls.Add(_openingVarietyCheckBox);

        optionsPanel.Controls.Add(new Label { Text = "Rand (%):", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _moveRandomnessNumeric = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = 10,
            Increment = 5,
            Width = 48,
            Enabled = false
        };
        _moveRandomnessNumeric.ValueChanged += (_, _) =>
        {
            if (_engine is MCTSEngine mcts)
            {
                mcts.Parameters.MoveRandomness = (int)_moveRandomnessNumeric.Value;
            }
        };
        optionsPanel.Controls.Add(_moveRandomnessNumeric);

        rightPanel.Controls.Add(optionsPanel);
        y += 122;

        // Action Buttons Row 1
        var btnPanel1 = new FlowLayoutPanel
        {
            Location = new Point(4, y),
            Size = new Size(360, 34),
            FlowDirection = FlowDirection.LeftToRight
        };

        var newGameBtn = new Button { Text = "New Game", AutoSize = true };
        newGameBtn.Click += (_, _) => ResetGame();
        btnPanel1.Controls.Add(newGameBtn);

        var engineMoveBtn = new Button { Text = "Engine Move", AutoSize = true };
        engineMoveBtn.Click += (_, _) => TriggerEngineMove();
        btnPanel1.Controls.Add(engineMoveBtn);

        var flipBoardBtn = new Button { Text = "Flip Board", AutoSize = true };
        flipBoardBtn.Click += (_, _) => _boardPanel.Flipped = !_boardPanel.Flipped;
        btnPanel1.Controls.Add(flipBoardBtn);

        var selfPlayBtn = new Button { Text = "Self Play", AutoSize = true };
        selfPlayBtn.Click += (_, _) => ToggleSelfPlay();
        btnPanel1.Controls.Add(selfPlayBtn);

        rightPanel.Controls.Add(btnPanel1);
        y += 38;

        // FEN Input row
        var fenPanel = new Panel
        {
            Location = new Point(4, y),
            Size = new Size(360, 30)
        };
        _fenTextBox = new TextBox
        {
            Location = new Point(0, 2),
            Size = new Size(220, 24),
            Text = Fen.StartPosition,
            Font = new Font("Consolas", 8.5f)
        };
        fenPanel.Controls.Add(_fenTextBox);

        var loadFenBtn = new Button
        {
            Text = "Load FEN",
            Location = new Point(226, 0),
            Size = new Size(70, 26)
        };
        loadFenBtn.Click += (_, _) => LoadFen(_fenTextBox.Text);
        fenPanel.Controls.Add(loadFenBtn);

        var copyFenBtn = new Button
        {
            Text = "Copy",
            Location = new Point(300, 0),
            Size = new Size(56, 26)
        };
        copyFenBtn.Click += (_, _) =>
        {
            Clipboard.SetText(Fen.ToFen(in _board));
            MessageBox.Show("FEN copied to clipboard!", "Anak Visualizer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        fenPanel.Controls.Add(copyFenBtn);

        rightPanel.Controls.Add(fenPanel);

        mainLayout.Controls.Add(rightPanel, 2, 0);
        Controls.Add(mainLayout);

        // Self-play timer
        _selfPlayTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _selfPlayTimer.Tick += (_, _) =>
        {
            if (!_isSearching && !_board.IsGameOver(out _))
            {
                TriggerEngineMove();
            }
        };

        // Digital clock timer & initial time control
        _clockTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _clockTimer.Tick += OnClockTimerTick;
        _clockTimer.Start();
        ResetTimeControl();

        UpdateGameDisplay();
    }

    private void OnEngineSelectionChanged()
    {
        if (_engineCombo.SelectedIndex == 0)
        {
            _engine = _alphaBetaEngine;
            _moveRandomnessNumeric.Enabled = false;
            _openingVarietyCheckBox.Enabled = false;
            _searchStatsLabel.Text = "Engine: Alpha-Beta Minimax (Idle)";
        }
        else
        {
            _mctsEngine ??= new MCTSEngine(_evaluator, defaultHashMb: 32, arenaCapacity: 1_000_000);
            _engine = _mctsEngine;
            _moveRandomnessNumeric.Enabled = true;
            _openingVarietyCheckBox.Enabled = true;
            _searchStatsLabel.Text = "Engine: MCTS (Idle)";
        }
    }

    private void ResetGame()
    {
        _searchCts?.Cancel();
        _selfPlayTimer.Stop();
        _alphaBetaEngine.TranspositionTable.Clear();
        _mctsEngine?.TranspositionTable.Clear();
        _mctsEngine?.Arena.Reset();
        _mctsEngine?.ClearHistory();
        ResetTimeControl();
        _board = Fen.Parse(Fen.StartPosition);
        _boardPanel.Board = _board;
        _boardPanel.LastMove = ChessMove.Null;
        _boardPanel.BestMove = ChessMove.Null;
        _topMovesListView.Items.Clear();
        _historyListBox.Items.Clear();
        _clockTimer.Start();
        UpdateGameDisplay();

        if (_engineWhiteCheckBox.Checked)
        {
            TriggerEngineMove();
        }
    }

    private void LoadFen(string fen)
    {
        try
        {
            _searchCts?.Cancel();
            _selfPlayTimer.Stop();
            _board = Fen.Parse(fen.Trim());
            _boardPanel.Board = _board;
            _boardPanel.LastMove = ChessMove.Null;
            _boardPanel.BestMove = ChessMove.Null;
            _topMovesListView.Items.Clear();
            _historyListBox.Items.Clear();
            _turnStopwatch.Restart();
            UpdateGameDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Invalid FEN: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleSelfPlay()
    {
        if (_selfPlayTimer.Enabled)
        {
            _selfPlayTimer.Stop();
            _statusLabel.Text = "Self-play paused";
        }
        else
        {
            _selfPlayTimer.Start();
            TriggerEngineMove();
        }
    }

    private void OnPlayerMoveMade(ChessMove move)
    {
        ApplyMove(move);

        // Auto-respond with engine if enabled
        bool isEngineTurn = (_board.SideToMove == ChessColor.Black && _engineBlackCheckBox.Checked) ||
                            (_board.SideToMove == ChessColor.White && _engineWhiteCheckBox.Checked);

        if (isEngineTurn && !_board.IsGameOver(out _))
        {
            TriggerEngineMove();
        }
    }

    private void ApplyMove(ChessMove move)
    {
        long turnElapsed = _turnStopwatch.ElapsedMilliseconds;
        _turnStopwatch.Restart();

        // Deduct elapsed time and credit increment to the mover
        if (_timeControlCombo.SelectedIndex != 4)
        {
            if (_board.SideToMove == ChessColor.White)
            {
                _whiteTimeMs = Math.Max(0, _whiteTimeMs - turnElapsed) + _incrementMs;
            }
            else
            {
                _blackTimeMs = Math.Max(0, _blackTimeMs - turnElapsed) + _incrementMs;
            }
            UpdateClockLabels();
        }

        string moveUci = move.ToUci();
        _board.MakeMove(move, out _);
        _boardPanel.Board = _board;
        _boardPanel.LastMove = move;
        _boardPanel.BestMove = ChessMove.Null;
        _topMovesListView.Items.Clear();

        _historyListBox.Items.Add($"{_historyListBox.Items.Count + 1}. {moveUci}");
        _historyListBox.TopIndex = _historyListBox.Items.Count - 1;

        UpdateGameDisplay();
    }

    private void UpdateGameDisplay()
    {
        _fenTextBox.Text = Fen.ToFen(in _board);

        // Check game state
        if (_board.IsGameOver(out GameResult result))
        {
            _statusLabel.Text = $"Game Over: {result}";
            _statusLabel.ForeColor = DrawingColor.DarkRed;
            _selfPlayTimer.Stop();
        }
        else if (_board.IsInCheck())
        {
            _statusLabel.Text = $"{_board.SideToMove} to move (IN CHECK!)";
            _statusLabel.ForeColor = DrawingColor.Crimson;
        }
        else
        {
            _statusLabel.Text = $"{_board.SideToMove} to move";
            _statusLabel.ForeColor = DrawingColor.FromArgb(40, 80, 160);
        }

        // Static evaluation
        MoveList moves = default;
        _board.GenerateLegalMoves(ref moves);
        Value val = _evaluator.Evaluate(in _board, moves.AsReadOnlySpan());

        int cp = val.Raw;
        int whiteScoreCp = _board.SideToMove == ChessColor.White ? cp : -cp;
        _evalBar.SetScore(whiteScoreCp, 0);

        string evalType = _evaluator is Anak.Data.Nnue.MultiTierNnueEvaluator nnue && nnue.IsActive ? "NNUE" : "Classical";
        double cpVal = whiteScoreCp / 100.0;
        string sign = whiteScoreCp > 0 ? "+" : "";
        _evalLabel.Text = $"Eval ({evalType}): {sign}{cpVal:0.00} ({whiteScoreCp:+#;-#;0} cp) (White Win%: {(int)(_evalBar.WhiteWinProbability * 100)}%)";
    }

    private async void TriggerEngineMove()
    {
        if (_isSearching) return;

        // 1. Opening book probe (if enabled)
        if (_useBookCheckBox.Checked && _book.IsLoaded)
        {
            Board bookBoard = _board;
            if (_book.TryGetMove(in bookBoard, out ChessMove bookMove, BookMode.WeightedRandom) && !bookMove.IsNull)
            {
                _searchStatsLabel.Text = $"Book Move: {bookMove.ToUci()} (instant)";
                _pvTextBox.Text = bookMove.ToUci();
                ApplyMove(bookMove);
                return;
            }
        }

        _isSearching = true;
        _engineCombo.Enabled = false;
        _searchCts = new CancellationTokenSource();

        SearchLimits limits;
        if (_timeControlCombo.SelectedIndex == 4) // Fixed Time (ms)
        {
            int thinkTimeMs = (int)_thinkTimeNumeric.Value;
            limits = new SearchLimits { MoveTime = thinkTimeMs };
            _searchStatsLabel.Text = $"Engine: Searching ({thinkTimeMs} ms fixed)...";
        }
        else
        {
            long turnElapsed = _turnStopwatch.ElapsedMilliseconds;
            int curWhite = (int)Math.Max(10, _whiteTimeMs - (_board.SideToMove == ChessColor.White ? turnElapsed : 0));
            int curBlack = (int)Math.Max(10, _blackTimeMs - (_board.SideToMove == ChessColor.Black ? turnElapsed : 0));

            limits = new SearchLimits
            {
                WhiteTime = curWhite,
                BlackTime = curBlack,
                WhiteIncrement = (int)_incrementMs,
                BlackIncrement = (int)_incrementMs
            };
            int myTime = _board.SideToMove == ChessColor.White ? curWhite : curBlack;
            _searchStatsLabel.Text = $"Engine: Thinking (Clock: {FormatTime(myTime)} +{_incrementMs / 1000:0}s)...";
        }

        Board searchBoard = _board;

        // Exploration temperature for opening moves if Opening Variety is checked (MCTS only)
        MCTSEngine? activeMcts = _engine as MCTSEngine;
        float originalTemp = 0.0f;
        bool applyVariety = _openingVarietyCheckBox.Checked && _historyListBox.Items.Count < 6;
        if (activeMcts != null)
        {
            originalTemp = activeMcts.Parameters.Temperature;
            if (applyVariety)
            {
                activeMcts.Parameters.Temperature = 0.35f;
            }
        }

        try
        {
            ChessMove bestMove = await Task.Run(() =>
            {
                return _engine.Search(
                    in searchBoard,
                    in limits,
                    stats =>
                    {
                        if (InvokeRequired)
                        {
                            BeginInvoke(new Action(() => UpdateSearchStats(stats)));
                        }
                        else
                        {
                            UpdateSearchStats(stats);
                        }
                    },
                    _searchCts.Token);
            });

            if (!bestMove.IsNull)
            {
                ApplyMove(bestMove);
            }
        }
        catch (OperationCanceledException)
        {
            _searchStatsLabel.Text = "Engine: Search stopped";
        }
        finally
        {
            if (activeMcts != null && applyVariety)
            {
                activeMcts.Parameters.Temperature = originalTemp;
            }
            _engineCombo.Enabled = true;
            _isSearching = false;
        }
    }

    private void UpdateSearchStats(SearchStatistics stats)
    {
        // 1. Live best move arrow on board
        ChessMove bestMove = !stats.BestMove.IsNull ? stats.BestMove : (stats.Pv is { Count: > 0 } ? stats.Pv[0] : ChessMove.Null);
        _boardPanel.BestMove = bestMove;

        // 2. Score formatting (centipawns e.g. +1.45 or mate distance M3 / -M2)
        string scoreDisplay;
        if (stats.MateIn != 0)
        {
            scoreDisplay = stats.MateIn > 0 ? $"M{stats.MateIn}" : $"-M{-stats.MateIn}";
        }
        else
        {
            double cpVal = stats.ScoreCp / 100.0;
            string sign = stats.ScoreCp > 0 ? "+" : "";
            scoreDisplay = $"{sign}{cpVal:0.00} ({stats.ScoreCp:+#;-#;0} cp)";
        }

        string cutoffDisplay = stats.BetaCutoffEfficiency.HasValue
            ? $" | Cutoff: {stats.BetaCutoffEfficiency.Value:F1}%"
            : "";

        // 3. Telemetry in real time
        _searchStatsLabel.Text =
            $"Depth: {stats.Depth}/{stats.SelDepth} | Nodes: {stats.Nodes:N0} | NPS: {stats.Nps:N0}\n" +
            $"Score: {scoreDisplay} | Hash: {stats.HashfullPermille}‰{cutoffDisplay}";

        // 4. Principal Variation (PV) string
        if (stats.Pv != null && stats.Pv.Count > 0)
        {
            _pvTextBox.Text = string.Join(' ', stats.Pv.Select(m => m.ToUci()));
        }

        // 5. Eval bar and evaluation label
        int whiteScoreCp = _board.SideToMove == ChessColor.White ? stats.ScoreCp : -stats.ScoreCp;
        int whiteMateIn = _board.SideToMove == ChessColor.White ? stats.MateIn : -stats.MateIn;
        _evalBar.SetScore(whiteScoreCp, whiteMateIn);
        _evalLabel.Text = $"Engine Eval: {scoreDisplay} (White Win%: {(int)(_evalBar.WhiteWinProbability * 100)}%)";

        // 6. Top 5 candidate moves table
        if (stats.TopMoves != null && stats.TopMoves.Count > 0)
        {
            _topMovesListView.BeginUpdate();
            _topMovesListView.Items.Clear();
            for (int i = 0; i < stats.TopMoves.Count; i++)
            {
                RootMoveCandidate cand = stats.TopMoves[i];
                string candMoveUci = cand.Move.ToUci();
                string candScore;
                if (cand.MateIn != 0)
                {
                    candScore = cand.MateIn > 0 ? $"M{cand.MateIn}" : $"-M{-cand.MateIn}";
                }
                else
                {
                    double cpVal = cand.ScoreCp / 100.0;
                    string sign = cand.ScoreCp > 0 ? "+" : "";
                    candScore = $"{sign}{cpVal:0.00} ({cand.ScoreCp:+#;-#;0})";
                }

                int whiteCp = _board.SideToMove == ChessColor.White ? cand.ScoreCp : -cand.ScoreCp;
                int winPct = (int)(EvalBarControl.CalculateWinProbability(whiteCp) * 100);
                string candPvStr = cand.Pv is { Count: > 0 } ? string.Join(' ', cand.Pv.Select(m => m.ToUci())) : candMoveUci;

                var lvi = new ListViewItem((i + 1).ToString());
                lvi.SubItems.Add(candMoveUci);
                lvi.SubItems.Add(candScore);
                lvi.SubItems.Add($"{winPct}%");
                lvi.SubItems.Add(candPvStr);
                lvi.Tag = cand;
                _topMovesListView.Items.Add(lvi);
            }
            _topMovesListView.EndUpdate();
        }
    }

    private void OnTopMoveSelected(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (e.IsSelected && e.Item?.Tag is RootMoveCandidate cand && !cand.Move.IsNull)
        {
            _boardPanel.BestMove = cand.Move;
        }
    }

    private void ResetTimeControl()
    {
        switch (_timeControlCombo.SelectedIndex)
        {
            case 0: // 5 min + 2s (Blitz)
                _whiteTimeMs = 300_000;
                _blackTimeMs = 300_000;
                _incrementMs = 2_000;
                _thinkTimeNumeric.Enabled = false;
                break;
            case 1: // 3 min + 2s (Blitz)
                _whiteTimeMs = 180_000;
                _blackTimeMs = 180_000;
                _incrementMs = 2_000;
                _thinkTimeNumeric.Enabled = false;
                break;
            case 2: // 1 min + 1s (Bullet)
                _whiteTimeMs = 60_000;
                _blackTimeMs = 60_000;
                _incrementMs = 1_000;
                _thinkTimeNumeric.Enabled = false;
                break;
            case 3: // 10 min + 5s (Rapid)
                _whiteTimeMs = 600_000;
                _blackTimeMs = 600_000;
                _incrementMs = 5_000;
                _thinkTimeNumeric.Enabled = false;
                break;
            case 4: // Fixed Time (ms)
                _whiteTimeMs = 0;
                _blackTimeMs = 0;
                _incrementMs = 0;
                _thinkTimeNumeric.Enabled = true;
                break;
        }

        _turnStopwatch.Restart();
        UpdateClockLabels();
    }

    private void UpdateClockLabels()
    {
        if (_timeControlCombo.SelectedIndex == 4)
        {
            _whiteClockLabel.Text = "White  Fixed";
            _blackClockLabel.Text = "Black  Fixed";
            return;
        }

        _whiteClockLabel.Text = $"White  {FormatTime(_whiteTimeMs)}";
        _blackClockLabel.Text = $"Black  {FormatTime(_blackTimeMs)}";
    }

    private static string FormatTime(double ms)
    {
        if (ms <= 0) return "00:00.0";
        if (ms >= 60_000)
        {
            int totalSec = (int)(ms / 1000);
            int m = totalSec / 60;
            int s = totalSec % 60;
            return $"{m:D2}:{s:D2}";
        }
        else
        {
            int totalTenths = (int)(ms / 100);
            int s = totalTenths / 10;
            int tenths = totalTenths % 10;
            return $"{s:D2}.{tenths:D1}";
        }
    }

    private void OnClockTimerTick(object? sender, EventArgs e)
    {
        if (_board.IsGameOver(out _))
        {
            return;
        }

        if (_timeControlCombo.SelectedIndex == 4)
        {
            _whiteClockLabel.Text = "White  Fixed";
            _blackClockLabel.Text = "Black  Fixed";
            return;
        }

        long elapsedTurn = _turnStopwatch.ElapsedMilliseconds;

        if (_board.SideToMove == ChessColor.White)
        {
            double rem = Math.Max(0, _whiteTimeMs - elapsedTurn);
            _whiteClockLabel.Text = $"White  {FormatTime(rem)}";
            _whiteClockPanel.BackColor = DrawingColor.FromArgb(235, 255, 235);
            _blackClockPanel.BackColor = DrawingColor.FromArgb(45, 45, 55);

            if (rem <= 0)
            {
                _clockTimer.Stop();
                _searchCts?.Cancel();
                _statusLabel.Text = "Game Over: White lost on time (flag fall)!";
                _statusLabel.ForeColor = DrawingColor.DarkRed;
            }
        }
        else
        {
            double rem = Math.Max(0, _blackTimeMs - elapsedTurn);
            _blackClockLabel.Text = $"Black  {FormatTime(rem)}";
            _blackClockPanel.BackColor = DrawingColor.FromArgb(50, 75, 60);
            _whiteClockPanel.BackColor = DrawingColor.White;

            if (rem <= 0)
            {
                _clockTimer.Stop();
                _searchCts?.Cancel();
                _statusLabel.Text = "Game Over: Black lost on time (flag fall)!";
                _statusLabel.ForeColor = DrawingColor.DarkRed;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _searchCts?.Cancel();
        _selfPlayTimer.Stop();
        _clockTimer.Stop();
        base.OnFormClosing(e);
    }
}
#endif
