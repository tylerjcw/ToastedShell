namespace Tosh.Stdlib.Plotting;

public sealed class Axis
{
    public string Label { get; set; } = string.Empty;
    public IScale Scale { get; set; }
    public (double Min, double Max)? ManualLimits { get; set; }
    public double MarginFraction { get; set; } = 0.05;

    public double Min => Scale.DataMin;
    public double Max => Scale.DataMax;

    public Axis(IScale? scale = null)
    {
        Scale = scale ?? new LinearScale();
    }

    public void SetLimits(double min, double max)
    {
        if (min >= max)
        {
            var mid = (min + max) / 2.0;
            min = mid - 0.5;
            max = mid + 0.5;
        }

        ManualLimits = (min, max);
        if (Scale is LinearScale lin)
        {
            lin.DataMin = min;
            lin.DataMax = max;
        }
        else if (Scale is LogScale log)
        {
            log.DataMin = min;
            log.DataMax = max;
        }
    }

    public void AutoScale(double dataMin, double dataMax)
    {
        if (ManualLimits.HasValue)
        {
            return;
        }

        if (double.IsNaN(dataMin) || double.IsInfinity(dataMin) ||
            double.IsNaN(dataMax) || double.IsInfinity(dataMax) || dataMin >= dataMax)
        {
            dataMin = 0.0;
            dataMax = 1.0;
        }

        var span = dataMax - dataMin;
        var margin = span * MarginFraction;
        var paddedMin = dataMin - margin;
        var paddedMax = dataMax + margin;

        if (Scale is LinearScale lin)
        {
            lin.DataMin = paddedMin;
            lin.DataMax = paddedMax;
        }
        else if (Scale is LogScale log)
        {
            log.DataMin = dataMin <= 0 ? 0.1 : dataMin * 0.9;
            log.DataMax = dataMax * 1.1;
        }
    }
}
