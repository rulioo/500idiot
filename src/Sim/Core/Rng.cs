namespace IdiotSim.Core;

/// <summary>
/// 确定性 xorshift128。design.md §5.2：禁止使用 UnityEngine.Random / System.Random，
/// 每个白痴持有独立流，seed = worldSeed * 1e6 + agentId，保证回放不漂移。
/// </summary>
public struct Rng
{
    private uint _a, _b, _c, _d;
    private bool _hasSpare;
    private float _spare;

    public Rng(uint seed)
    {
        uint s = seed == 0 ? 0x9E3779B9u : seed;
        _a = s | 1u;
        _b = (s * 69069u) + 1u;
        _c = s ^ 0x9E3779B9u;
        _d = (s * 2654435761u) | 1u;
        _hasSpare = false;
        _spare = 0f;
        // 预热，避免低位种子相似导致前几个输出相关
        for (int i = 0; i < 8; i++) NextUInt();
    }

    public uint NextUInt()
    {
        uint t = _d;
        uint s = _a;
        _d = _c;
        _c = _b;
        _b = s;

        t ^= t << 11;
        t ^= t >> 8;
        _a = t ^ s ^ (s >> 19);
        return _a;
    }

    /// <summary>[0,1)</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    public int RangeInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        uint span = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt() % span);
    }

    public bool Chance(float p) => NextFloat() < p;

    /// <summary>Box-Muller 标准正态（缓存第二个样本）。</summary>
    public float NextGaussian()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        float u, v, s;
        do
        {
            u = NextFloat() * 2f - 1f;
            v = NextFloat() * 2f - 1f;
            s = u * u + v * v;
        } while (s >= 1f || s == 0f);
        float mul = MathF.Sqrt(-2f * MathF.Log(s) / s);
        _spare = v * mul;
        _hasSpare = true;
        return u * mul;
    }

    /// <summary>截断正态：均值 mu、标准差 sigma、限制在 [lo, hi]。</summary>
    public float GaussianClamped(float mu, float sigma, float lo, float hi)
    {
        float x = mu + NextGaussian() * sigma;
        if (x < lo) x = lo;
        if (x > hi) x = hi;
        return x;
    }

    public Vec2 NextDir()
    {
        float ang = NextFloat() * MathF.Tau;
        return new Vec2(MathF.Cos(ang), MathF.Sin(ang));
    }
}
