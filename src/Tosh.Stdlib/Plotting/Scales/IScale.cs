namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Maps continuous data values to display pixels.
/// </summary>
public interface IScale
{
    double DataMin { get; set; }
    double DataMax { get; set; }
    float PixelMin { get; set; }
    float PixelMax { get; set; }
    ITransform1D Transform { get; }

    float ToPixel(double data);
    double ToData(float pixel);

    IReadOnlyList<double> GenerateTicks(int targetCount = 5);
    string FormatTick(double value);
}
