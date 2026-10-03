using IdiotSim.Core;

namespace IdiotSim.World;

/// <summary>世界中的可交互实体。</summary>
public sealed class Entity
{
    public int Id;
    public EntityType Type;
    public int Index;            // 同类中的序号（从 1 开始，用于"电暖气#3"这类显示）
    public Vec2 Pos;
    public float Height;         // 插座：距地高度；其他道具：中心高度

    // --- 通用持有 ---
    public bool Held;
    public int HolderId = -1;
    public bool InPool;          // 掉进水里了：捞不回来，也不能再当目标

    // --- 插座 ---
    public int Wall;             // 0=北(z=+50) 1=东(x=+50) 2=南(z=-50) 3=西(x=-50)
    public Vec2 WallInward;      // 墙面法线（指向屋内）
    public Vec2 SocketRight;     // 插座自身的"右手边"世界方向（design.md §2.4）
    public SocketState SState = SocketState.Empty;
    public int PluggedHeater = -1;
    public int NailEntityId = -1;

    // --- 电暖气 ---
    public float Yaw;            // 朝向（渲染用）
    public bool Plugged;
    public int PluggedSocket = -1;
    public int Knob;             // 0 / 1 / 2
    public bool Running;         // 已通电且档位 >= 1
    /// <summary>温控器断开（室温到了就停，掉下来再接上）。真实电暖气都带这个。</summary>
    public bool ThermostatCut;
    public float HeatRamp;       // 0..1 预热进度
    public float SurfaceTemp;    // 外壳温度（渲染 + 烫伤判定）
    public bool Broken;          // 落水损坏

    // --- 火 ---
    public float Fuel;

    public bool IsProp => Type is EntityType.Nail or EntityType.Plank or EntityType.Stick or EntityType.Weed;

    /// <summary>该实体是否属于"道具"（进入尝试记录，design.md §6.5.2）。</summary>
    public bool IsAttemptSubject => Type != EntityType.Fire;
}
