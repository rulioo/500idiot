using IdiotSim.Core;
using IdiotSim.Recording;

namespace IdiotSim.World;

/// <summary>
/// 世界构建（design.md §2）。所有布局由 worldSeed 确定性生成。
/// </summary>
public static class Arena
{
    /// <summary>北墙 0 / 东墙 1 / 南墙 2 / 西墙 3。</summary>
    public static Vec2 WallInward(int wall) => wall switch
    {
        0 => new Vec2(0, -1),   // 北墙 z=+50，面朝屋内（-Z）
        1 => new Vec2(-1, 0),   // 东墙 x=+50
        2 => new Vec2(0, 1),    // 南墙 z=-50
        _ => new Vec2(1, 0),    // 西墙 x=-50
    };

    public static Vec2 WallPoint(int wall, float along, float half)
        => wall switch
        {
            0 => new Vec2(along, half),
            1 => new Vec2(half, along),
            2 => new Vec2(along, -half),
            _ => new Vec2(-half, along),
        };

    /// <summary>沿墙坐标范围：-45,-35,...,+45（10 列）。</summary>
    public static float ColumnCoord(int i) => -45f + i * 10f;

    public static void Build(Simulation sim)
    {
        var cfg = sim.Cfg;
        float half = cfg.RoomHalf;
        var rng = new Rng((uint)(cfg.WorldSeed * 7919 + 13));
        int eid = 1;

        // ---------- 400 个插座：4 面墙 × (10 列 × 10 行) ----------
        for (int wall = 0; wall < 4; wall++)
        {
            var inward = WallInward(wall);
            // 面对插座站立时的朝向 = -inward；其右手边 = (-inward).Right
            var faceDir = -inward;
            var sockRight = faceDir.Right;

            int idx = 0;
            for (int col = 0; col < 10; col++)
            {
                float along = ColumnCoord(col);
                for (int row = 0; row < 10; row++)
                {
                    idx++;
                    float h = 0.30f + row * 0.078f;
                    sim.Entities.Add(new Entity
                    {
                        Id = eid,
                        Type = EntityType.Socket,
                        Index = wall * 100 + idx,
                        Pos = WallPoint(wall, along, half),
                        Height = h,
                        Wall = wall,
                        WallInward = inward,
                        SocketRight = sockRight,
                    });
                    eid++;
                }
            }
        }
        sim.SocketIds = new List<int>();
        foreach (var e in sim.Entities) if (e.Type == EntityType.Socket) sim.SocketIds.Add(e.Id);

        // ---------- 10 台电暖气：沿四壁散落，距最近插座 3~4m（§2.5.1）----------
        // 位置 = 与某个插座列同 along 坐标，距墙 3.5m。需推 1.5m 才够得着（电源线 2.0m）。
        (int wall, float along)[] heaterSpots =
        {
            (0, -45f), (0, 5f), (0, 35f),
            (1, -45f), (1, 5f),
            (2, -25f), (2, 25f),
            (3, -25f), (3, 25f), (3, 45f),
        };
        for (int i = 0; i < heaterSpots.Length; i++)
        {
            var (wall, along) = heaterSpots[i];
            var p = WallPoint(wall, along, half - 3.5f);
            sim.Entities.Add(new Entity
            {
                Id = eid,
                Type = EntityType.Heater,
                Index = i + 1,
                Pos = p,
                Height = 0.35f,
                Yaw = MathF.Atan2(-WallInward(wall).X, -WallInward(wall).Z),
                SurfaceTemp = cfg.T_Out,
            });
            sim.HeaterIds.Add(eid);
            eid++;
        }

        // ---------- 10 根铁钉 / 10 块木板 / 10 根木棍 / 40 堆杂草：沿墙脚散落 ----------
        void Scatter(EntityType type, int count, float insetMin, float insetMax, List<int> into)
        {
            for (int i = 0; i < count; i++)
            {
                float along = rng.Range(-47f, 47f);
                int wall = rng.RangeInt(0, 4);
                float inset = rng.Range(insetMin, insetMax);
                var p = WallPoint(wall, along, half - inset);
                sim.Entities.Add(new Entity
                {
                    Id = eid,
                    Type = type,
                    Index = i + 1,
                    Pos = p,
                    Height = 0.05f,
                });
                into?.Add(eid);
                eid++;
            }
        }

        Scatter(EntityType.Nail, 10, 0.8f, 1.6f, sim.NailIds);
        Scatter(EntityType.Plank, 10, 1.0f, 3.0f, sim.PlankIds);
        Scatter(EntityType.Stick, 10, 1.0f, 3.0f, sim.StickIds);
        Scatter(EntityType.Weed, 40, 0.6f, 2.5f, sim.WeedIds);

        sim.EntityById = new Entity[sim.Entities.Count + 1];
        foreach (var e in sim.Entities) sim.EntityById[e.Id] = e;

        // ---------- 500 个白痴 ----------
        for (int i = 1; i <= cfg.AgentCount; i++)
        {
            var a = new Agents.Agent
            {
                Id = i,
                Pos = new Vec2(rng.Range(-46f, 46f), rng.Range(-46f, 46f)),
                Rng = new Rng((uint)(cfg.WorldSeed * 1000003 + i * 7919)),
            };
            // 避免出生在水池里
            while (MathF.Abs(a.Pos.X) < cfg.PoolHalf + 1.5f && MathF.Abs(a.Pos.Z) < cfg.PoolHalf + 1.5f)
                a.Pos = new Vec2(rng.Range(-46f, 46f), rng.Range(-46f, 46f));

            a.Facing = a.Rng.NextDir();
            Params(a, cfg);
            a.CoreTemp = 36.2f + a.Rng.Range(-0.2f, 0.4f);
            a.LocalTemp = cfg.T_Out;
            a.Warmth = 1f;
            a.Comfort = 0f;
            a.LearnAction(ActionType.Idle);
            a.LearnAction(ActionType.Wander);
            a.LearnAction(ActionType.Cry);
            a.LearnAction(ActionType.Approach);
            a.LearnAction(ActionType.TouchProp);
            a.LearnAction(ActionType.Grab);
            a.LearnAction(ActionType.DropHeld);
            a.LearnAction(ActionType.Flee);
            a.LearnAction(ActionType.Rest);
            sim.Agents.Add(a);

            sim.Events.Add(new EventRecord
            {
                Tick = 0, AgentId = (ushort)i, Type = EventType.Spawn, Pos = a.Pos, Value = i
            });
        }
    }

    /// <summary>个体参数（design.md §3.6）。perseveration 与 curiosity 弱负相关 r≈-0.2。</summary>
    private static void Params(Agents.Agent a, SimConfig c)
    {
        var r = a.Rng;
        float curiosity = r.GaussianClamped(c.CuriosityMean, c.CuriositySd, 0.05f, 1f);
        float zPers = r.NextGaussian();
        float persev = c.PerseverationMean + c.PerseverationSd * (-0.2f * ((curiosity - c.CuriosityMean) / MathF.Max(c.CuriositySd, 1e-4f))
                                                                  + 0.9798f * zPers);
        a.PCuriosity = curiosity;
        a.PPerseveration = Math.Clamp(persev, 0.03f, 1f);
        a.PBoldness = r.GaussianClamped(c.BoldnessMean, c.BoldnessSd, 0.02f, 1f);
        a.PImitation = r.GaussianClamped(c.ImitationMean, c.ImitationSd, 0.05f, 1f);
        a.PAttentionSpan = r.GaussianClamped(c.AttentionSpanMean, c.AttentionSpanSd, 8f, 70f);
        a.PNoiseSigma = r.GaussianClamped(c.NoiseMean, c.NoiseSd, 0.05f, 0.8f);
        a.PSocial = r.GaussianClamped(c.SocialMean, c.SocialSd, 0.02f, 1f);
        a.PFearThreshold = r.GaussianClamped(c.FearMean, c.FearSd, 0.1f, 1f);
        a.PPainTolerance = r.GaussianClamped(c.PainTolMean, c.PainTolSd, 0.05f, 1f);
    }
}
