namespace IdiotSim;

/// <summary>
/// 全部可调参数（design.md §9）。所有数值运行时可变——整个项目的成败取决于调参。
/// </summary>
public sealed class SimConfig
{
    // ---------- 世界 ----------
    public float RoomHalf = 50f;          // 房间 100x100
    public float RoomHeight = 10f;
    public float PoolHalf = 5f;           // 水池 10x10，居中
    public float PoolDepth = 10f;
    public float PoolSurfaceY = -0.3f;
    public int AgentCount = 500;
    public int SocketsPerWall = 100;      // 每面墙 100 个 -> 全屋 400

    // ---------- 热（design.md §2.2）----------
    public float C_HeatCapacity = 4.5e6f;   // J/K
    public float K_Dissipation = 3000f;     // W/K
    public float T_Out = 15f;               // 初始/室外温度
    public float T_Max = 35f;
    public float Q_Heater = 3000f;          // W/台
    public float Q_Fire = 25000f;           // W
    public float Q_Pool = -2000f;           // W（冷源）
    public float Q_Body = 10f;              // W/人
    public float HeaterRampSeconds = 30f;   // 预热
    // 电暖气自带温控器（问过现实：所有家用取暖器都有）。没有它，
    // 10 台 3kW + 一堆火把屋子直接顶到 T_Max 35°C，舒适区变成一次性事件，
    // G3 一过就再没人舒服过——那不叫"全屋舒适"，那叫中暑。
    public float HeaterThermostatOff = 27f; // 高于此温度断电
    public float HeaterThermostatOn = 24f;  // 低于此温度复电
    public float HeaterLocalDeltaT = 6f;    // 局部热点
    public float HeaterLocalRadius = 4f;
    public float FireLocalDeltaT = 12f;
    public float FireLocalRadius = 8f;
    public float FireBurnSeconds = 5400f;   // 90 游戏分钟

    public float ComfortLow = 20f;          // 舒适区间下沿
    public float ComfortHigh = 26f;

    // ---------- 时间（design.md §5.1）----------
    public float TickSeconds = 0.25f;       // 仿真步长（游戏秒）
    public int BrainEveryTicks = 4;         // 大脑 1 游戏秒一次
    public int BodyEveryTicks = 16;         // 身体 4 游戏秒一次
    public int TrajEveryTicks = 4;          // 轨迹 1 游戏秒
    public int BodySampleEveryTicks = 40;   // 身体采样 10 游戏秒
    public float RealSecondsPerGameSecond = 1f / 60f;  // 1 真实秒 = 60 游戏秒

    // ---------- 感知（design.md §3.2）----------
    public float SightRadius = 25f;
    public float SocialRadius = 18f;        // 注意得到多远的同类（比看东西近）
    public float SightHalfAngleDeg = 70f;   // 140° 锥形
    public float ClarityThreshold = 0.35f;  // 低于此只写记忆不生成信念
    public float ClarityResolveThreshold = 0.80f; // 高于此才能看清是哪个孔
    public float MirrorProbMidRange = 0.22f;      // 中等清晰度下的左右误判率（§2.4 参照系）

    // ---------- 认知（design.md §4.2）----------
    public float DecayPeriodSeconds = 300f;
    public float DecayFactor = 0.97f;
    public float ForgetBelow = 0.05f;
    public int GeneralizeInstances = 3;
    public float ConfPersonalSuccess = 0.60f;
    public float ConfPersonalBurn = 0.90f;
    public float ConfPersonalWarm = 0.30f;
    public float ConfWarmReinforce = 0.05f;   // 每 60 游戏秒
    public float ConfObserveSuccess = 0.25f;
    public float ConfObserveDeath = 0.40f;
    public float ConfObserveBurn = 0.35f;
    public float ConfImitate = 0.05f;

    // ---------- 身体（design.md §3.7）----------
    public float BodyTau = 900f;            // 体温时间常数
    // 一次触碰烫伤 ≈ 20 次才致命。原先 15：7 次就死，而开场 20 分钟全屋 15°C、
    // 恢复又要求"舒适"（§3.7.1），于是那 20 分钟里每一次烫伤都是净负债——
    // 500 个冻得发抖的人围着 10 台电暖气，最后 251 人（一半）是这么烫死的，
    // 比水池（129）和铁钉（74）加起来还多。烫伤该是伤害，不该是处决。
    public float BurnDamage = 5f;           // 烫伤扣生命
    public float BurnPain = 0.90f;
    public float FallDamage = 10f;
    public float FallPain = 0.50f;
    public float PushPain = 0.20f;
    public float PainDecayPerSecond = 0.02f;
    // 恢复只在"舒适"时发生（§3.7.1），这条保留——冷的时候伤口就是好不了。
    // 但原先的速率是 1/600s，也就是 10 游戏分钟回 1 点：等于没有恢复。
    // 于是开场那 20 分钟全屋 15°C、人人往电暖气上扑、全是烫伤又完全不回血，
    // 一次烫伤就是永久负债，攒够 7 次就死——3 小时里 255 人（51%）是这么没的，
    // 比水池和铁钉加起来还多。一次烫伤应该在几分钟的温暖里缓过来。
    public float VitalityRegenPer600s = 25f;
    public float BaseInsertError = 0.05f;   // 基础插孔失误率

    // ---------- 行为权重（design.md §3.4）----------
    public float W_Warm = 1.0f;
    public float W_Curio = 0.80f;
    public float W_Imit = 0.90f;
    public float W_Fam = 0.50f;
    public float W_Safe = 1.20f;
    public float W_Social = 0.30f;
    public float W_Dist = 0.020f;           // 每米
    public float HerdBonus = 1.5f;          // >=3 人同动作
    public float FearOverride = 0.70f;
    public float PainOverride = 0.50f;

    // ---------- 动作 ----------
    public float WalkSpeed = 1.2f;          // m / 游戏秒
    public float RunSpeed = 2.0f;
    public float ReachRange = 0.8f;
    public float SocketReach = 0.9f;
    public float HeaterPowerCordLength = 2.0f;
    public float HeaterPushPerAction = 0.4f;   // 每次推动移动距离（米）
    public float DrillRequiredTicks = 180f;    // 累计需要的搓动时长（tick 数 × TickSeconds = 45 游戏秒，design.md §2.5.3 S4）
    public float DrillSecondsPerAction = 5f;   // 每一次"搓"折算多少游戏秒的进度
    public float DrillBroadcastEvery = 15f;    // 每累计这么多游戏秒才向外广播一次"他在搓"
    public float FireFuelRadius = 6f;          // 引火物（杂草）要在这个半径内
    public float FireMinWeeds = 1;

    // ---------- 个体参数分布（design.md §3.6）----------
    public float CuriosityMean = 0.60f, CuriositySd = 0.20f;
    public float BoldnessMean = 0.50f, BoldnessSd = 0.25f;
    public float ImitationMean = 0.65f, ImitationSd = 0.20f;
    public float PerseverationMean = 0.40f, PerseverationSd = 0.25f;
    public float AttentionSpanMean = 30f, AttentionSpanSd = 10f;
    public float NoiseMean = 0.30f, NoiseSd = 0.10f;
    public float SocialMean = 0.50f, SocialSd = 0.20f;
    public float FearMean = 0.60f, FearSd = 0.20f;
    public float PainTolMean = 0.50f, PainTolSd = 0.20f;

    public int WorldSeed = 20261003;

    public static SimConfig Default => new();

    public SimConfig Clone() => (SimConfig)MemberwiseClone();

    /// <summary>1 游戏秒 = 1/TickSeconds tick。</summary>
    public float TicksPerSecond => 1f / TickSeconds;
    public int SecondsToTicks(float sec) => (int)MathF.Round(sec / TickSeconds);
}
