using System;

namespace HighOrbitAI
{
    /// <summary>
    /// 超軽量タグ表現：文字列Tagではなくビットフラグで判定する。
    /// 条件付き（ドア/液体など）もフラグで表現し、状態は Volume 側で切り替える。
    /// </summary>
    [Flags]
    public enum NavFlags : ushort
    {
        None        = 0,
        Blocked     = 1 << 0, // 物理的に通れない（壁など）
        KeepOut     = 1 << 1, // 侵入禁止（硬い）
        SoftAvoid   = 1 << 2, // できれば避ける（コスト加算）
        Dynamic     = 1 << 3, // 動的（AABB/状態が更新対象）

        // ---- Condition / Semantic ----
        Door        = 1 << 4, // ドア由来
        Hazard      = 1 << 5, // 液体/危険地帯由来
        Conditional = 1 << 6, // 条件で状態が変わり得るVolume
    }
}
