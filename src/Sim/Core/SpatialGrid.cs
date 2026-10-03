namespace IdiotSim.Core;

/// <summary>
/// 均匀网格空间划分（design.md §8.2）。
/// 用于感知邻居查询与圆形碰撞。每帧重建，零 GC（复用 List）。
///
/// 格子边长可调：实体（几乎不动，分布稀疏）用 10m 格子，
/// 白痴用 5m 格子——500 个人挤在运转的电暖气旁边时，
/// 10m 格子会让一次 Query 吐出上千个候选，直接拖垮整个仿真。
/// </summary>
public sealed class SpatialGrid
{
    public readonly float CellSize;
    public readonly int Cols;
    public readonly int Rows;

    private readonly List<int>[] _cells;
    private readonly List<int> _scratch = new(256);

    public SpatialGrid(float cellSize = 10f, float halfExtent = 50f)
    {
        CellSize = cellSize;
        Cols = Math.Max(1, (int)MathF.Ceiling(halfExtent * 2f / cellSize));
        Rows = Cols;
        _cells = new List<int>[Cols * Rows];
        for (int i = 0; i < _cells.Length; i++) _cells[i] = new List<int>(16);
    }

    public void Clear()
    {
        for (int i = 0; i < _cells.Length; i++) _cells[i].Clear();
    }

    private int CellIndex(Vec2 p)
    {
        int cx = (int)((p.X + 50f) / CellSize);
        int cz = (int)((p.Z + 50f) / CellSize);
        if (cx < 0) cx = 0; else if (cx >= Cols) cx = Cols - 1;
        if (cz < 0) cz = 0; else if (cz >= Rows) cz = Rows - 1;
        return cz * Cols + cx;
    }

    public void Insert(int index, Vec2 pos) => _cells[CellIndex(pos)].Add(index);

    /// <summary>
    /// 查询半径内的候选索引（不精确，返回格子内容，调用方需自行判距）。
    /// maxResults &gt; 0 时提前收手——感知只需要"近处的几十个人"，不需要全部。
    /// </summary>
    public List<int> Query(Vec2 center, float radius, int maxResults = 0)
    {
        _scratch.Clear();
        int span = (int)MathF.Ceiling(radius / CellSize);
        if (span < 0) span = 0;
        int cx = (int)((center.X + 50f) / CellSize);
        int cz = (int)((center.Z + 50f) / CellSize);
        int x0 = Math.Max(0, cx - span), x1 = Math.Min(Cols - 1, cx + span);
        int z0 = Math.Max(0, cz - span), z1 = Math.Min(Rows - 1, cz + span);
        for (int z = z0; z <= z1; z++)
        {
            int rowBase = z * Cols;
            for (int x = x0; x <= x1; x++)
            {
                _scratch.AddRange(_cells[rowBase + x]);
                if (maxResults > 0 && _scratch.Count >= maxResults) return _scratch;
            }
        }
        return _scratch;
    }
}
