// Traces: MATRIX-RAIN-FIELD (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Rain;

namespace SavviedMatrix.Tests;

public class RainFieldTests
{
    private const int Rows = 54;

    [Fact]
    public void NothingIsVisibleBeforeTheStreamEnters()
    {
        var stream = new StreamParams(DelaySeconds: 0.5, RowsPerSecond: 20, TrailLength: 8);

        for (int row = 0; row < Rows; row++)
            Assert.Equal(CellPhase.Hidden, RainField.StateAt(stream, row, 0.0).Phase);
    }

    [Fact]
    public void AtTimeZeroWithNoDelayOnlyTheTopRowIsTheHead()
    {
        var stream = new StreamParams(0, 20, 8);

        Assert.Equal(CellPhase.Head, RainField.StateAt(stream, 0, 0.0).Phase);
        Assert.Equal(CellPhase.Hidden, RainField.StateAt(stream, 1, 0.0).Phase);
    }

    [Fact]
    public void TheHeadSitsAtTheExpectedRow()
    {
        var stream = new StreamParams(0, 10, 8);

        // At 2.05 seconds and 10 rows per second the head is inside row 20.
        Assert.Equal(CellPhase.Head, RainField.StateAt(stream, 20, 2.05).Phase);
        Assert.Equal(CellPhase.Hidden, RainField.StateAt(stream, 21, 2.05).Phase);
    }

    [Fact]
    public void RowsJustBehindTheHeadAreTrailWithIncreasingDistance()
    {
        var stream = new StreamParams(0, 10, 5);

        for (int d = 1; d < 5; d++)
        {
            var state = RainField.StateAt(stream, 20 - d, 2.05);
            Assert.Equal(CellPhase.Trail, state.Phase);
            Assert.Equal(d, state.TrailDistance);
        }
    }

    [Fact]
    public void RowsBeyondTheTrailHaveSettled()
    {
        var stream = new StreamParams(0, 10, 5);

        Assert.Equal(CellPhase.Settled, RainField.StateAt(stream, 15, 2.05).Phase);
        Assert.Equal(CellPhase.Settled, RainField.StateAt(stream, 0, 2.05).Phase);
    }

    [Fact]
    public void EveryCellIsInExactlyOnePhase()
    {
        var streams = StreamParamsFactory.Create(64, Rows, 3.0, new Random(11));
        var buffer = new CellState[64 * Rows];

        for (double t = 0; t <= 6.0; t += 0.13)
        {
            RainField.Evaluate(streams, 64, Rows, t, buffer);

            foreach (var state in buffer)
            {
                Assert.True(Enum.IsDefined(state.Phase));
                if (state.Phase != CellPhase.Trail)
                    Assert.Equal(0, state.TrailDistance);
                else
                    Assert.InRange(state.TrailDistance, 1, StreamParamsFactory.MaxTrail - 1);
            }
        }
    }

    [Fact]
    public void EvaluateAgreesWithTheSingleCellFunction()
    {
        var streams = StreamParamsFactory.Create(16, 20, 2.0, new Random(5));
        var buffer = new CellState[16 * 20];

        RainField.Evaluate(streams, 16, 20, 1.1, buffer);

        for (int col = 0; col < 16; col++)
        for (int row = 0; row < 20; row++)
            Assert.Equal(RainField.StateAt(streams[col], row, 1.1), buffer[row * 16 + col]);
    }

    [Fact]
    public void EveryColumnHasSettledByItsSettleTime()
    {
        var streams = StreamParamsFactory.Create(128, Rows, 3.0, new Random(23));
        double settle = StreamParamsFactory.TotalSettleTime(streams, Rows);

        Assert.True(RainField.IsComplete(streams, Rows, settle));

        var buffer = new CellState[128 * Rows];
        RainField.Evaluate(streams, 128, Rows, settle, buffer);
        Assert.All(buffer, s => Assert.Equal(CellPhase.Settled, s.Phase));
    }

    [Fact]
    public void TheFieldIsNotCompleteBeforeTheSlowestColumnLands()
    {
        var streams = StreamParamsFactory.Create(128, Rows, 3.0, new Random(23));
        double settle = StreamParamsFactory.TotalSettleTime(streams, Rows);

        Assert.False(RainField.IsComplete(streams, Rows, settle - 0.05));
    }

    [Fact]
    public void GeneratedStreamsStayWithinTheDocumentedBounds()
    {
        var streams = StreamParamsFactory.Create(200, Rows, 3.0, new Random(77));

        Assert.All(streams, s =>
        {
            Assert.InRange(s.DelaySeconds, 0, StreamParamsFactory.MaxDelayFraction * 3.0);
            Assert.InRange(s.TrailLength, StreamParamsFactory.MinTrail, StreamParamsFactory.MaxTrail);
            Assert.True(s.RowsPerSecond > 0);
        });
    }

    [Fact]
    public void EveryColumnLandsCloseToTheRequestedDuration()
    {
        const double seconds = 3.0;
        var streams = StreamParamsFactory.Create(200, Rows, seconds, new Random(31));

        // Jitter is plus or minus fifteen per cent, so no column should be wildly early or late.
        Assert.All(streams, s =>
        {
            double settle = s.SettleTime(Rows);
            Assert.InRange(settle, seconds * 0.7, seconds * 1.5);
        });
    }

    [Fact]
    public void GenerationIsDeterministicForAGivenSeed()
    {
        var a = StreamParamsFactory.Create(64, Rows, 3.0, new Random(4));
        var b = StreamParamsFactory.Create(64, Rows, 3.0, new Random(4));

        Assert.Equal(a, b);
    }

    [Fact]
    public void TimeAtRowInvertsHeadAt()
    {
        var stream = new StreamParams(0.3, 17.5, 9);

        double t = stream.TimeAtRow(30);

        Assert.Equal(30.0, stream.HeadAt(t), 6);
    }
}
