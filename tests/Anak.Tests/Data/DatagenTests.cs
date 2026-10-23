using Anak.Core;
using Anak.Data.Datagen;
using Anak.Protocol.Datagen;
using Xunit;
using SysFile = System.IO.File;
using SysDirectory = System.IO.Directory;

namespace Anak.Tests.Data;

public class DatagenTests
{
    [Fact]
    public void AnakBinpackHeader_HasCorrectSizeAndMagic()
    {
        var header = AnakBinpackHeader.Create("open", 42);
        Assert.Equal("ANAKBINP", header.GetMagicString());
        Assert.Equal("open", header.GetModelTagString());
        Assert.Equal(1u, header.Version);
        Assert.Equal(144u, header.RecordSize);
        Assert.Equal(42ul, header.RecordCount);
    }

    [Fact]
    public void AnakBinpackRecord_EncodesStartPositionProperly()
    {
        var board = Fen.Parse(Fen.StartPosition);
        var record = AnakBinpackRecord.FromBoard(in board, targetEval: 25.0f, targetWdl: 0.5f, isVolatile: false);

        Assert.Equal(32, record.PieceCount);
        Assert.Equal(25.0f, record.TargetEval);
        Assert.Equal(0.5f, record.TargetWdl);
        Assert.True(record.Openness < 0.50f); // Starting position is closed
        Assert.Equal(0x02, record.Flags); // bit 1 set: has_queens
    }

    [Fact]
    public void BinpackWriter_And_Reader_RoundtripProperly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"anak_test_{Guid.NewGuid():N}.binpack");

        try
        {
            var board = Fen.Parse(Fen.StartPosition);

            using (var writer = new BinpackWriter(tempFile, "test", bufferSize: 16))
            {
                for (int i = 0; i < 50; i++)
                {
                    var rec = AnakBinpackRecord.FromBoard(in board, targetEval: i * 10f, targetWdl: 0.5f, isVolatile: i % 5 == 0);
                    writer.Append(in rec);
                }
            }

            Assert.True(SysFile.Exists(tempFile));
            var fileInfo = new FileInfo(tempFile);
            Assert.Equal(64 + (50 * 144), fileInfo.Length);

            using (var reader = new BinpackReader(tempFile))
            {
                Assert.Equal(50ul, reader.TotalRecords);
                Assert.Equal("ANAKBINP", reader.Header.GetMagicString());
                Assert.Equal("test", reader.Header.GetModelTagString());

                int count = 0;
                while (reader.ReadNext(out var rec))
                {
                    Assert.Equal(count * 10f, rec.TargetEval);
                    Assert.Equal(0.5f, rec.TargetWdl);
                    Assert.Equal(32, rec.PieceCount);
                    count++;
                }

                Assert.Equal(50, count);
            }
        }
        finally
        {
            if (SysFile.Exists(tempFile))
            {
                SysFile.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void StratifiedBinpackWriter_RoutesToCorrectPacks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"anak_strat_{Guid.NewGuid():N}");

        try
        {
            using (var writer = new StratifiedBinpackWriter(tempDir, microSampleRate: 2, exportEpd: true))
            {
                // 1. Closed position (startpos)
                var closedBoard = Fen.Parse(Fen.StartPosition);
                writer.WritePosition(in closedBoard, 0f, 0.5f, isVolatile: false);

                // 2. Open position (few center pawns)
                var openBoard = Fen.Parse("rnbqkbnr/pp2pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2");
                writer.WritePosition(in openBoard, 15f, 0.5f, isVolatile: false);

                // 3. Queenless position
                var qlessBoard = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1");
                writer.WritePosition(in qlessBoard, 0f, 0.5f, isVolatile: false);

                // 4. Endgame position (<= 14 pieces)
                var egBoard = Fen.Parse("4k3/4p3/8/8/8/8/4P3/4K3 w - - 0 1");
                writer.WritePosition(in egBoard, 50f, 1.0f, isVolatile: true); // also volatile
            }

            Assert.True(SysFile.Exists(Path.Combine(tempDir, "closed.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "open.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "queenless.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "endgame.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "volatility.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "micro.binpack")));
            Assert.True(SysFile.Exists(Path.Combine(tempDir, "training.epd")));
        }
        finally
        {
            if (SysDirectory.Exists(tempDir))
            {
                SysDirectory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DatagenWorker_PlaysGameAndOutputsPositions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"anak_worker_{Guid.NewGuid():N}");

        try
        {
            var options = new DatagenOptions
            {
                Games = 1,
                Threads = 1,
                Depth = 2,
                Nodes = 50,
                RandomPlies = 2,
                MaxPliesPerGame = 15,
                OutputDir = tempDir
            };

            using (var writer = new StratifiedBinpackWriter(tempDir, microSampleRate: 1))
            {
                var worker = new DatagenWorker(0, options, writer);
                float? wdl = worker.PlayOneGame(CancellationToken.None);

                Assert.NotNull(wdl);
                Assert.True(wdl.Value is 0.0f or 0.5f or 1.0f);
                Assert.True(writer.TotalPositions > 0);
            }

            Assert.True(SysFile.Exists(Path.Combine(tempDir, "closed.binpack")));
        }
        finally
        {
            if (SysDirectory.Exists(tempDir))
            {
                SysDirectory.Delete(tempDir, recursive: true);
            }
        }
    }
}
