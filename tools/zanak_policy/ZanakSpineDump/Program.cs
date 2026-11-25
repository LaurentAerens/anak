using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anak.Core;
using Zanak.Search;

namespace ZanakSpineDump;

internal sealed record SpinePlyRecord(
    [property: JsonPropertyName("fen_idx")] int FenIdx,
    [property: JsonPropertyName("ply")] int Ply,
    [property: JsonPropertyName("fen")] string Fen,
    [property: JsonPropertyName("zanak_move")] string ZanakMove,
    [property: JsonPropertyName("zanak_eval_before")] int ZanakEvalBefore,
    [property: JsonPropertyName("zanak_eval_after")] int ZanakEvalAfter,
    [property: JsonPropertyName("static_eval_delta")] int StaticEvalDelta,
    [property: JsonPropertyName("is_capture")] bool IsCapture,
    [property: JsonPropertyName("is_check")] bool IsCheck,
    [property: JsonPropertyName("is_promotion")] bool IsPromotion,
    [property: JsonPropertyName("prev_was_capture")] bool PrevWasCapture,
    [property: JsonPropertyName("dest_attacked_after")] bool DestAttackedAfter,
    [property: JsonPropertyName("see_cp")] int SeeCp,
    [property: JsonPropertyName("max_threatened_before")] int MaxThreatenedBefore,
    [property: JsonPropertyName("legal_count")] int LegalCount,
    [property: JsonPropertyName("in_check_before")] bool InCheckBefore,
    [property: JsonPropertyName("is_recapture")] bool IsRecapture,
    [property: JsonPropertyName("moved_piece_type")] string MovedPieceType,
    [property: JsonPropertyName("from_attacked_before")] bool FromAttackedBefore,
    [property: JsonPropertyName("from_defended_before")] bool FromDefendedBefore,
    [property: JsonPropertyName("dest_defended_after")] bool DestDefendedAfter);

[JsonSerializable(typeof(SpinePlyRecord))]
internal sealed partial class SpineDumpJsonContext : JsonSerializerContext
{
}

internal static class Program
{
    private static int Main(string[] args)
    {
        string? inputPath = null;
        string? outputPath = null;
        int maxPlies = 10;
        List<string> inlineFens = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--input" || arg == "-i") && i + 1 < args.Length)
            {
                inputPath = args[++i];
            }
            else if ((arg == "--output" || arg == "-o") && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (arg == "--max-plies" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out maxPlies) || maxPlies <= 0)
                {
                    Console.Error.WriteLine("Invalid --max-plies value.");
                    return 2;
                }
            }
            else if (arg == "--help" || arg == "-h")
            {
                Console.Error.WriteLine("Usage: ZanakSpineDump [--input <epd/fen file>] [--output <jsonl>] [--max-plies <10>] [FEN...]");
                return 0;
            }
            else
            {
                inlineFens.Add(arg);
            }
        }

        List<string> rawLines = [];
        if (inputPath != null)
        {
            foreach (string line in System.IO.File.ReadLines(inputPath, Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }
                rawLines.Add(trimmed);
            }
        }
        else if (inlineFens.Count > 0)
        {
            rawLines.AddRange(inlineFens);
        }
        else
        {
            while (Console.In.ReadLine() is { } line)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }
                rawLines.Add(trimmed);
            }
        }

        using ZanakNnueEvaluator evaluator = new();
        if (!evaluator.IsActive)
        {
            Console.Error.WriteLine("Error: ZanakNnueEvaluator failed to load NNUE networks.");
            return 1;
        }

        ZapPolicy policy = new();

        TextWriter writer = outputPath != null
            ? new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            : Console.Out;

        try
        {
            for (int fenIdx = 0; fenIdx < rawLines.Count; fenIdx++)
            {
                string normalizedFen = NormalizeFen(rawLines[fenIdx]);
                Board board = Fen.Parse(normalizedFen);
                evaluator.InitSearch(in board);
                Move lastMove = Move.Null;

                for (int ply = 0; ply < maxPlies; ply++)
                {
                    MoveList legal = default;
                    MoveGenerator.GenerateLegalMoves(in board, ref legal);
                    if (legal.Count == 0)
                    {
                        break;
                    }

                    int evalBefore = (int)evaluator.Evaluate(in board);
                    if (!policy.TryGetQuickMove(in board, ply, lastMove, out Move move) || move.IsNull)
                    {
                        break;
                    }

                    string fenBefore = Fen.ToFen(in board);
                    bool inCheckBefore = board.IsInCheck();
                    bool isCapture = move.IsCapture;
                    bool isCheck = board.GivesCheck(move);
                    bool isPromotion = move.IsPromotion;
                    bool prevWasCapture = !lastMove.IsNull && lastMove.IsCapture;
                    bool isRecapture = prevWasCapture && isCapture && move.To == lastMove.To;
                    int seeCp = StaticExchangeEvaluator.Evaluate(in board, move);
                    int maxThreatenedBefore = ZapPolicy.GetMaxThreatenedPiece(in board, out _);
                    Piece movedPiece = board.GetPieceAt(move.From);
                    Color us = board.SideToMove;
                    Color them = us.Opponent();
                    bool fromAttackedBefore = board.IsSquareAttacked(move.From, them);
                    bool fromDefendedBefore = board.IsSquareAttacked(move.From, us);

                    if (!board.MakeMove(move, out BoardState state, movedPiece))
                    {
                        break;
                    }

                    evaluator.PushMove(move, movedPiece, state.CapturedPiece, in board);
                    int rawAfter = (int)evaluator.Evaluate(in board);
                    int evalAfter = -rawAfter;
                    int staticEvalDelta = evalAfter - evalBefore;
                    bool destAttackedAfter = board.IsSquareAttacked(move.To, board.SideToMove);
                    bool destDefendedAfter = board.IsSquareAttacked(move.To, board.SideToMove.Opponent());

                    SpinePlyRecord record = new(
                        FenIdx: fenIdx,
                        Ply: ply,
                        Fen: fenBefore,
                        ZanakMove: move.ToUci(),
                        ZanakEvalBefore: evalBefore,
                        ZanakEvalAfter: evalAfter,
                        StaticEvalDelta: staticEvalDelta,
                        IsCapture: isCapture,
                        IsCheck: isCheck,
                        IsPromotion: isPromotion,
                        PrevWasCapture: prevWasCapture,
                        DestAttackedAfter: destAttackedAfter,
                        SeeCp: seeCp,
                        MaxThreatenedBefore: maxThreatenedBefore,
                        LegalCount: legal.Count,
                        InCheckBefore: inCheckBefore,
                        IsRecapture: isRecapture,
                        MovedPieceType: movedPiece.Type().ToString().ToLowerInvariant(),
                        FromAttackedBefore: fromAttackedBefore,
                        FromDefendedBefore: fromDefendedBefore,
                        DestDefendedAfter: destDefendedAfter);

                    writer.WriteLine(JsonSerializer.Serialize(record, SpineDumpJsonContext.Default.SpinePlyRecord));
                    lastMove = move;
                }
            }
        }
        finally
        {
            if (outputPath != null)
            {
                writer.Dispose();
            }
        }

        return 0;
    }

    private static string NormalizeFen(string source)
    {
        string[] fields = source.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            throw new FormatException($"Expected at least 4 FEN/EPD fields, got: '{source}'");
        }

        if (fields.Length >= 6 && int.TryParse(fields[4], out _) && int.TryParse(fields[5], out _))
        {
            return string.Join(' ', fields[..6]);
        }

        return $"{fields[0]} {fields[1]} {fields[2]} {fields[3]} 0 1";
    }
}
