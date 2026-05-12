namespace MacMonitor.Web;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Loopback by default; do NOT change to 0.0.0.0 unless you've added auth.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>HTTP port. Default: 5050.</summary>
    public int Port { get; set; } = 5050;
}
