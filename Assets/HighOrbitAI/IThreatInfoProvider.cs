namespace HighOrbitAI
{
    public interface IThreatInfoProvider
    {
        /// <summary>HP比率（0..1, 1=満タン）</summary>
        float Hp01 { get; }

        /// <summary>武装の危険度（0..1）例：近接特化=高、単発小銃=低、範囲武器=高</summary>
        float WeaponThreat01 { get; }

        /// <summary>ロックオン/追尾などの“狙われている感”（0..1）</summary>
        float LockOnThreat01 { get; }
    }
}
