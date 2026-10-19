#if WINDOWS
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Anak.Core;
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
    private readonly ListBox _historyListBox;
    private readonly TextBox _fenTextBox;
    private readonly NumericUpDown _thinkTimeNumeric;
    private readonly CheckBox _engineBlackCheckBox;
    private readonly CheckBox _engineWhiteCheckBox;
    private readonly System.Windows.Forms.Timer _selfPlayTimer;

    private readonly MCTSEngine _engine;
    private readonly IEvaluator _evaluator;
    private CancellationTokenSource? _searchCts;
    private bool _isSearching;

    public MainForm()
    {
        Text = "Anak Chess Engine — Local Debugging & Visualizer";
        Size = new Size(1020, 720);
        MinimumSize = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        BackColor = DrawingColor.FromArgb(245, 245, 248);

        _board = Fen.Parse(Fen.StartPosition);
        _evaluator = UciEngineAdapter.CreateEvaluator();
        _engine = new MCTSEngine(_evaluator, defaultHashMb: 32, arenaCapacity: 1_000_000);

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
            Text = "Local visualizer using Anak (MCTS & Magic Bitboards)\nAdapted from open-source C# chess GUI designs (MIT License)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = DrawingColor.FromArgb(100, 100, 115),
            Location = new Point(4, y),
            Size = new Size(360, 32)
        };
        rightPanel.Controls.Add(creditsLabel);
        y += 36;

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
            Text = "Eval: 0 cp (50% winrate)",
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
            Text = "Engine: Idle",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = DrawingColor.FromArgb(70, 70, 80),
            Location = new Point(4, y),
            Size = new Size(360, 36)
        };
        rightPanel.Controls.Add(_searchStatsLabel);
        y += 40;

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

        // Engine Options: Think time & checkboxes
        var optionsPanel = new FlowLayoutPanel
        {
            Location = new Point(4, y),
            Size = new Size(360, 32),
            FlowDirection = FlowDirection.LeftToRight
        };

        optionsPanel.Controls.Add(new Label { Text = "Time (ms):", AutoSize = true, Margin = new Padding(0, 4, 4, 0) });
        _thinkTimeNumeric = new NumericUpDown
        {
            Minimum = 50,
            Maximum = 30000,
            Value = 500,
            Increment = 100,
            Width = 70
        };
        optionsPanel.Controls.Add(_thinkTimeNumeric);

        _engineWhiteCheckBox = new CheckBox
        {
            Text = "Engine White",
            AutoSize = true,
            Checked = false,
            Margin = new Padding(8, 2, 0, 0)
        };
        optionsPanel.Controls.Add(_engineWhiteCheckBox);

        _engineBlackCheckBox = new CheckBox
        {
            Text = "Engine Black",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(4, 2, 0, 0)
        };
        optionsPanel.Controls.Add(_engineBlackCheckBox);

        rightPanel.Controls.Add(optionsPanel);
        y += 36;

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

        UpdateGameDisplay();
    }

    private void ResetGame()
    {
        _searchCts?.Cancel();
        _selfPlayTimer.Stop();
        _board = Fen.Parse(Fen.StartPosition);
        _boardPanel.Board = _board;
        _boardPanel.LastMove = ChessMove.Null;
        _historyListBox.Items.Clear();
        UpdateGameDisplay();
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
            _historyListBox.Items.Clear();
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
        string moveUci = move.ToUci();
        _board.MakeMove(move, out _);
        _boardPanel.Board = _board;
        _boardPanel.LastMove = move;

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
        int displayScore = _board.SideToMove == ChessColor.White ? cp : -cp;

        // Calculate win probability from centipawns: 1 / (1 + 10^(-cp / 400))
        double winProb = 1.0 / (1.0 + Math.Pow(10, -displayScore / 400.0));
        _evalBar.WhiteWinProbability = winProb;

        _evalLabel.Text = $"Eval: {displayScore:+#;-#;0} cp (White Win% : {(int)(winProb * 100)}%)";
    }

    private async void TriggerEngineMove()
    {
        if (_isSearching) return;

        _isSearching = true;
        _searchCts = new CancellationTokenSource();

        int thinkTimeMs = (int)_thinkTimeNumeric.Value;
        var limits = new SearchLimits { MoveTime = thinkTimeMs };
        Board searchBoard = _board;

        _searchStatsLabel.Text = $"Engine: Searching ({thinkTimeMs} ms)...";

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
            _isSearching = false;
        }
    }

    private void UpdateSearchStats(SearchStatistics stats)
    {
        _searchStatsLabel.Text = $"Depth: {stats.Depth}/{stats.SelDepth} | Nodes: {stats.Nodes:N0} | NPS: {stats.Nps:N0}";
        if (stats.Pv != null && stats.Pv.Count > 0)
        {
            _pvTextBox.Text = string.Join(' ', stats.Pv.Select(m => m.ToUci()));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _searchCts?.Cancel();
        _selfPlayTimer.Stop();
        base.OnFormClosing(e);
    }
}
#endif
