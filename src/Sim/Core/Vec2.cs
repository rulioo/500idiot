namespace IdiotSim.Core;

/// <summary>水平面二维向量（世界为 XZ 平面，Y 为高度）。</summary>
public struct Vec2
{
    public float X, Z;

    public Vec2(float x, float z) { X = x; Z = z; }

    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Z + b.Z);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Z - b.Z);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Z * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Z * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Z / s);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Z);

    public float Length => MathF.Sqrt(X * X + Z * Z);
    public float LengthSq => X * X + Z * Z;

    public Vec2 Normalized
    {
        get { float l = Length; return l > 1e-6f ? new Vec2(X / l, Z / l) : new Vec2(0, 0); }
    }

    /// <summary>绕 -90° 旋转，得到"面朝此方向时，我的右手边"。见 design.md §2.4 参照系。</summary>
    public Vec2 Right => new(Z, -X);

    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;

    /// <summary>带符号夹角（弧度）。</summary>
    public static float AngleBetween(Vec2 a, Vec2 b)
    {
        float d = Dot(a.Normalized, b.Normalized);
        if (d > 1f) d = 1f; else if (d < -1f) d = -1f;
        return MathF.Acos(d);
    }

    public static float Dist(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static float DistSq(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);

    public override string ToString() => $"({X:F1}, {Z:F1})";
}
