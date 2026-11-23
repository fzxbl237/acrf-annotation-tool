namespace AcrfAnnotationTool.Controls;

/// <summary>Physical spring parameters (damped harmonic oscillator).</summary>
internal readonly record struct MorphSpring(double Stiffness, double Damping, double Mass = 1.0)
{
    public static MorphSpring FromVisual(double visualDuration, double bounce)
    {
        if (visualDuration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visualDuration));
        }
        var root = 2 * Math.PI / (1.2 * visualDuration);
        var stiffness = root * root;
        var zeta = Math.Clamp(1 - bounce, 0.05, 1);
        return new MorphSpring(stiffness, 2 * zeta * Math.Sqrt(stiffness));
    }

    public static MorphSpring FromPhysical(double stiffness, double damping, double mass = 1.0)
    {
        if (stiffness <= 0 || damping <= 0 || mass <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stiffness), "Spring parameters must be positive.");
        }
        return new MorphSpring(stiffness, damping, mass);
    }

    public double NaturalFrequency => Math.Sqrt(Stiffness / Mass);

    public double DampingRatio => Damping / (2 * Math.Sqrt(Stiffness * Mass));
}

/// <summary>Closed-form solution of the damped harmonic oscillator.</summary>
internal static class MorphSpringSolver
{
    public static (double Displacement, double Velocity) Evaluate(in MorphSpring spec, double x0, double v0, double t)
    {
        var w0 = spec.NaturalFrequency;
        var zeta = spec.DampingRatio;
        if (Math.Abs(zeta - 1) < 1e-6)
        {
            var e = Math.Exp(-w0 * t);
            var bCoef = v0 + w0 * x0;
            var x = e * (x0 + bCoef * t);
            var v = e * (bCoef - w0 * (x0 + bCoef * t));
            return (x, v);
        }
        if (zeta < 1)
        {
            var wd = w0 * Math.Sqrt(1 - zeta * zeta);
            var e = Math.Exp(-zeta * w0 * t);
            var bCoef = (v0 + zeta * w0 * x0) / wd;
            var cos = Math.Cos(wd * t);
            var sin = Math.Sin(wd * t);
            var x = e * (x0 * cos + bCoef * sin);
            var v = e * ((bCoef * wd - zeta * w0 * x0) * cos - (x0 * wd + zeta * w0 * bCoef) * sin);
            return (x, v);
        }
        {
            var wh = w0 * Math.Sqrt(zeta * zeta - 1);
            var e = Math.Exp(-zeta * w0 * t);
            var bCoef = (v0 + zeta * w0 * x0) / wh;
            var cosh = Math.Cosh(wh * t);
            var sinh = Math.Sinh(wh * t);
            var x = e * (x0 * cosh + bCoef * sinh);
            var v = e * ((bCoef * wh - zeta * w0 * x0) * cosh + (x0 * wh - zeta * w0 * bCoef) * sinh);
            return (x, v);
        }
    }
}
