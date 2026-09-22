// Traces: MATRIX-GRID-FIT (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Ascii;

namespace SavviedMatrix.Tests;

public class GridFitTests
{
    // The shipping geometry: 1080p at 192 columns gives 10x20 pixel cells.
    private const int Cols = 192;
    private const int Rows = 54;
    private const int CellW = 10;
    private const int CellH = 20;

    [Fact]
    public void SixteenByNineImageFillsTheWholeGrid()
    {
        var fit = GridFit.Fit(1920, 1080, Cols, Rows, CellW, CellH);

        Assert.Equal(192, fit.FitCols);
        Assert.Equal(54, fit.FitRows);
        Assert.Equal(0, fit.OffsetCol);
        Assert.Equal(0, fit.OffsetRow);
    }

    [Fact]
    public void SquareImageIsLimitedByHeightAndCentredHorizontally()
    {
        var fit = GridFit.Fit(1000, 1000, Cols, Rows, CellW, CellH);

        // Cell aspect is 1:2, so a square image needs twice as many columns as rows.
        Assert.Equal(54, fit.FitRows);
        Assert.Equal(108, fit.FitCols);
        Assert.Equal(42, fit.OffsetCol);
        Assert.Equal(0, fit.OffsetRow);
    }

    [Fact]
    public void PortraitImageIsLimitedByHeight()
    {
        var fit = GridFit.Fit(1000, 2000, Cols, Rows, CellW, CellH);

        Assert.Equal(54, fit.FitRows);
        Assert.Equal(54, fit.FitCols);
        Assert.Equal(69, fit.OffsetCol);
        Assert.Equal(0, fit.OffsetRow);
    }

    [Fact]
    public void UltraWideImageIsLimitedByWidthAndCentredVertically()
    {
        var fit = GridFit.Fit(4000, 1000, Cols, Rows, CellW, CellH);

        Assert.Equal(192, fit.FitCols);
        Assert.Equal(24, fit.FitRows);
        Assert.Equal(0, fit.OffsetCol);
        Assert.Equal(15, fit.OffsetRow);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1000, 1000)]
    [InlineData(640, 4800)]
    [InlineData(4800, 640)]
    [InlineData(3, 7)]
    [InlineData(7, 3)]
    public void FitNeverExceedsTheScreenGrid(int imageWidth, int imageHeight)
    {
        var fit = GridFit.Fit(imageWidth, imageHeight, Cols, Rows, CellW, CellH);

        Assert.InRange(fit.FitCols, 1, Cols);
        Assert.InRange(fit.FitRows, 1, Rows);
        Assert.True(fit.OffsetCol + fit.FitCols <= Cols);
        Assert.True(fit.OffsetRow + fit.FitRows <= Rows);
        Assert.True(fit.OffsetCol >= 0);
        Assert.True(fit.OffsetRow >= 0);
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1000, 1000)]
    [InlineData(800, 1200)]
    [InlineData(2400, 1000)]
    public void DisplayedAspectRatioMatchesTheImageWithinOneCell(int imageWidth, int imageHeight)
    {
        var fit = GridFit.Fit(imageWidth, imageHeight, Cols, Rows, CellW, CellH);

        double imageAspect = (double)imageWidth / imageHeight;
        double displayedAspect = (double)(fit.FitCols * CellW) / (fit.FitRows * CellH);

        // Rounding to whole cells is the only permitted error.
        double tolerance = imageAspect * (1.0 / Math.Min(fit.FitCols, fit.FitRows));
        Assert.True(
            Math.Abs(displayedAspect - imageAspect) <= tolerance,
            $"aspect {displayedAspect:F4} differs from {imageAspect:F4} by more than {tolerance:F4}");
    }

    [Fact]
    public void SquareCellsChangeTheFitAccordingly()
    {
        // With square cells a square image should fill a square region of cells.
        var fit = GridFit.Fit(1000, 1000, 100, 100, 10, 10);

        Assert.Equal(100, fit.FitCols);
        Assert.Equal(100, fit.FitRows);
    }

    [Fact]
    public void ContainsMarksTheLetterboxAsOutside()
    {
        var fit = GridFit.Fit(1000, 1000, Cols, Rows, CellW, CellH);

        Assert.False(fit.Contains(0, 0));
        Assert.True(fit.Contains(fit.OffsetCol, fit.OffsetRow));
        Assert.True(fit.Contains(fit.OffsetCol + fit.FitCols - 1, fit.OffsetRow + fit.FitRows - 1));
        Assert.False(fit.Contains(fit.OffsetCol + fit.FitCols, fit.OffsetRow));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void RejectsDegenerateImageDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GridFit.Fit(width, height, Cols, Rows, CellW, CellH));
    }
}
