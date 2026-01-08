using System;

namespace HighOrbitAI
{
    /// <summary>
    /// 超軽量タグ表現：文字列Tagではなくビットフラグで判定する。
    /// </summary>
    [Flags]
    public enum NavFlags : ushort
    {
        None      = 0,
        Blocked   = 1 << 0, // 物理的に通れない（壁など）
        KeepOut   = 1 << 1, // 侵入禁止（硬い）
        SoftAvoid = 1 << 2, // できれば避ける（コスト加算）
        Dynamic   = 1 << 3, // 動的（更新対象）
    }
}
