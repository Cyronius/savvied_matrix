using SavviedMatrix.Core;

namespace SavviedMatrix.Ascii;

/// <summary>
/// Works out how many character cells an image should occupy so that it keeps its
/// aspect ratio. Character cells are taller than they are wide, so the grid's aspect
/// and the image's aspect are not the same thing and the cell shape has to be folded in.
/// </summary>
public static class GridFit
{
    /// <summary>
    /// Largest placement of a <paramref name="imageWidth"/> x <paramref name="imageHeight"/>
    /// image inside a <paramref name="cols"/> x <paramref name="rows"/> character grid whose
    /// cells are <paramref name="cellWidth"/> x <paramref name="cellHeight"/> pixels,
    /// centred, preserving aspect ratio.
    /// </summary>
    public static GridPlacement Fit(
        int imageWidth, int imageHeight,
        int cols, int rows,
        int cellWidth, int cellHeight)
    {
        if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeight));
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (cellWidth <= 0) throw new ArgumentOutOfRangeException(nameof(cellWidth));
        if (cellHeight <= 0) throw new ArgumentOutOfRangeException(nameof(cellHeight));

        // Columns per row that reproduces the image's aspect ratio once the
        // non-square character cell is taken into account.
        double colsPerRow = (double)imageWidth / imageHeight * cellHeight / cellWidth;

        int fitCols, fitRows;

        // Try full width first; fall back to full height when that would overflow.
        double rowsIfFullWidth = cols / colsPerRow;
        if (rowsIfFullWidth <= rows)
        {
            fitCols = cols;
            fitRows = (int)Math.Round(rowsIfFullWidth, MidpointRounding.AwayFromZero);
        }
        else
        {
            fitRows = rows;
            fitCols = (int)Math.Round(rows * colsPerRow, MidpointRounding.AwayFromZero);
        }

        fitCols = Math.Clamp(fitCols, 1, cols);
        fitRows = Math.Clamp(fitRows, 1, rows);

        int offsetCol = (cols - fitCols) / 2;
        int offsetRow = (rows - fitRows) / 2;

        return new GridPlacement(fitCols, fitRows, offsetCol, offsetRow);
    }
}
