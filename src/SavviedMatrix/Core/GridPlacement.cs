namespace SavviedMatrix.Core;

/// <summary>
/// Where an image's character grid sits inside the full screen grid.
/// Everything outside [OffsetCol, OffsetCol + FitCols) x [OffsetRow, OffsetRow + FitRows)
/// is letterbox and renders as black.
/// </summary>
public readonly record struct GridPlacement(int FitCols, int FitRows, int OffsetCol, int OffsetRow)
{
    public bool Contains(int col, int row)
        => col >= OffsetCol && col < OffsetCol + FitCols
        && row >= OffsetRow && row < OffsetRow + FitRows;
}
