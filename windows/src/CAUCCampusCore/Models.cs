using System.Globalization;
using System.Text.Json.Serialization;

namespace CAUCCampusCore;

public static class CampusEndpoints
{
    public static readonly Uri PortalUri = new("http://192.168.100.200/");
    public const string ChkstatusBase = "http://192.168.100.200/drcom/chkstatus";
    public static readonly Uri IPv6TriggerUri = new("http://test6.ustc.edu.cn/");
    public static readonly Uri UstcIPv6Probe = new("https://test6.ustc.edu.cn/");

    public static readonly IReadOnlyList<Uri> PublicTargets =
    [
        new("https://www.cloudflare.com/cdn-cgi/trace"),
        new("https://www.apple.com/library/test/success.html"),
    ];
}

public readonly record struct ConnectivityStatus(bool IPv4 = false, bool IPv6 = false)
{
    public bool IsDualStackOnline => IPv4 && IPv6;
}

public sealed record CampusQuota
{
    public string? AccountID { get; init; }
    public double? BalanceYuan { get; init; }
    public ulong? RemainingKB { get; init; }
    public bool IsUnlimitedFlow { get; init; }
    public ulong? UsedKB { get; init; }
    public ulong? IPv6UsedKB { get; init; }
    public ulong? OnlineMinutes { get; init; }
    public string? IPv4Address { get; init; }
    public string? IPv6Address { get; init; }

    [JsonIgnore]
    public string RemainingText
    {
        get
        {
            if (IsUnlimitedFlow)
            {
                return "不限量";
            }

            return RemainingKB is { } remaining ? FormatTraffic(remaining) : "--";
        }
    }

    [JsonIgnore]
    public string BalanceText => BalanceYuan is { } yuan ? FormatCurrency(yuan) : "--";

    [JsonIgnore]
    public string? UsedText => UsedKB is { } used ? FormatTraffic(used) : null;

    [JsonIgnore]
    public string? IPv6UsedText => IPv6UsedKB is { } v6Used ? FormatTraffic(v6Used) : null;

    [JsonIgnore]
    public bool IsLowRemainingFlow => RemainingKB is < 1_048_576;

    [JsonIgnore]
    public bool IsLowBalance => BalanceYuan is < 5.0;

    public static string FormatTraffic(ulong kb)
    {
        if (kb < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{kb} KB");
        }

        if (kb < 1_048_576)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{kb / 1024.0:F1} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{kb / 1_048_576.0:F2} GB");
    }

    public static string FormatCurrency(double yuan)
    {
        return string.Create(CultureInfo.InvariantCulture, $"¥{yuan:F2}");
    }
}

public sealed record CachedCampusQuota(CampusQuota Quota, DateTimeOffset UpdatedAt);

public enum PortalQueryKind
{
    Online,
    Offline,
    Unreachable,
}

public sealed record PortalQueryResult(PortalQueryKind Kind, CampusQuota? Quota = null)
{
    public static PortalQueryResult Offline { get; } = new(PortalQueryKind.Offline);
    public static PortalQueryResult Unreachable { get; } = new(PortalQueryKind.Unreachable);
    public static PortalQueryResult Online(CampusQuota quota) => new(PortalQueryKind.Online, quota);
}
