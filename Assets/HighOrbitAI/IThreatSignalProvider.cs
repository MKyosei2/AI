namespace HighOrbitAI
{
    public interface IThreatSignalProvider
    {
        /// <summary>直近で被弾/危険を感じている（0..1）</summary>
        float UnderFire01 { get; }

        /// <summary>敵にロック/追尾されている感（任意・0..1）</summary>
        float Targeted01 { get; }
    }
}
