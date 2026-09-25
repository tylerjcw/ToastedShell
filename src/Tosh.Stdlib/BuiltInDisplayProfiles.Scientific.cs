using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tosh.Stdlib.Shell;
using Tosh.Stdlib.Sys;
using Tosh.Runtime;
using Tosh.Stdlib.Net;

namespace Tosh.Stdlib;

public static partial class BuiltInDisplayProfiles
{
    internal static void RegisterScientificProfiles(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        registry.Register(CreateBigIntegerProfile());
        registry.Register(CreateComplexProfile());
        registry.Register(CreateVector2Profile());
        registry.Register(CreateVector3Profile());
        registry.Register(CreateVector4Profile());
        registry.Register(CreateQuaternionProfile());
        registry.Register(CreateMatrix4x4Profile());
        registry.Register(CreateFigureProfile());
        registry.Register(CreateSymExprProfile());
    }

    private static DisplayProfile CreateBigIntegerProfile()
    {
        return DisplayProfile
            .For<BigInteger>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((BigInteger)context.Value).ToString(CultureInfo.InvariantCulture));
    }

    private static DisplayProfile CreateComplexProfile()
    {
        return DisplayProfile
            .For<Complex>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Real", row => ((Complex)row).Real.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Imaginary", row => ((Complex)row).Imaginary.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 20, Priority: 10),
                    new DisplayTableColumn("Magnitude", row => ((Complex)row).Magnitude.ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Phase", row => ((Complex)row).Phase.ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 30),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var c = (Complex)context.Value;
                    var sign = c.Imaginary >= 0 ? "+" : "-";
                    return $"{c.Real.ToString(CultureInfo.InvariantCulture)} {sign} {Math.Abs(c.Imaginary).ToString(CultureInfo.InvariantCulture)}i";
                });
    }

    // `TOAST-0061`. The vectors and the quaternion print as values — `Vector3(1, 2, 3)` —
    // rather than as a transposed table of components. They are values: twelve bytes with
    // names, not records with fields, and a reader asking for one wants to see it rather than
    // read a four-row table to learn three numbers. The type leads the form because
    // `(1, 2, 3)` alone does not say whether the fourth component was dropped or never there.
    //
    // A matrix keeps its table. Sixteen numbers on one line is not a reading of anything.
    private static DisplayProfile CreateVector2Profile()
    {
        return DisplayProfile
            .For<Vector2>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector2)context.Value;
                    return $"Vector2({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateVector3Profile()
    {
        return DisplayProfile
            .For<Vector3>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector3)context.Value;
                    return $"Vector3({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)}, {v.Z.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateVector4Profile()
    {
        return DisplayProfile
            .For<Vector4>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector4)context.Value;
                    return $"Vector4({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)}, {v.Z.ToString(CultureInfo.InvariantCulture)}, {v.W.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateQuaternionProfile()
    {
        return DisplayProfile
            .For<Quaternion>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var q = (Quaternion)context.Value;
                    return $"Quaternion({q.W.ToString(CultureInfo.InvariantCulture)}; {q.X.ToString(CultureInfo.InvariantCulture)}, {q.Y.ToString(CultureInfo.InvariantCulture)}, {q.Z.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateMatrix4x4Profile()
    {
        return DisplayProfile
            .For<Matrix4x4>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("M11", row => ((Matrix4x4)row).M11.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("M12", row => ((Matrix4x4)row).M12.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 1, CanHide: false),
                    new DisplayTableColumn("M13", row => ((Matrix4x4)row).M13.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 2, CanHide: false),
                    new DisplayTableColumn("M14", row => ((Matrix4x4)row).M14.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 3, CanHide: false),
                    new DisplayTableColumn("M21", row => ((Matrix4x4)row).M21.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 4),
                    new DisplayTableColumn("M22", row => ((Matrix4x4)row).M22.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 5),
                    new DisplayTableColumn("M23", row => ((Matrix4x4)row).M23.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 6),
                    new DisplayTableColumn("M24", row => ((Matrix4x4)row).M24.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 7),
                    new DisplayTableColumn("M31", row => ((Matrix4x4)row).M31.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 8),
                    new DisplayTableColumn("M32", row => ((Matrix4x4)row).M32.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 9),
                    new DisplayTableColumn("M33", row => ((Matrix4x4)row).M33.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("M34", row => ((Matrix4x4)row).M34.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 11),
                    new DisplayTableColumn("M41", row => ((Matrix4x4)row).M41.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 12),
                    new DisplayTableColumn("M42", row => ((Matrix4x4)row).M42.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 13),
                    new DisplayTableColumn("M43", row => ((Matrix4x4)row).M43.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 14),
                    new DisplayTableColumn("M44", row => ((Matrix4x4)row).M44.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 15),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context => "Matrix4x4");
    }

    // ── WebProxy ─────────────────────────────────────────────────────────

    private static DisplayProfile CreateFigureProfile()
    {
        return DisplayProfile
            .For<Tosh.Stdlib.Plotting.Figure>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((Tosh.Stdlib.Plotting.Figure)context.Value).ToTerminalString());
    }

    private static DisplayProfile CreateSymExprProfile()
    {
        return DisplayProfile
            .For<Tosh.Stdlib.Cas.SymExpr>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((Tosh.Stdlib.Cas.SymExpr)context.Value).ToString());
    }

}
