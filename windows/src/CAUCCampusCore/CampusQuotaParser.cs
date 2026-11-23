using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CAUCCampusCore;

public static class CampusQuotaParser
{
    private static readonly Encoding LossyUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false
    );

    private static ReadOnlySpan<byte> CrlfSeparator => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> LfSeparator => "\n\n"u8;

    public static PortalQueryResult? ParseHTTPResponse(ReadOnlySpan<byte> rawHTTP)
    {
        int separatorLen;
        int separatorIndex = rawHTTP.IndexOf(CrlfSeparator);
        if (separatorIndex >= 0)
        {
            separatorLen = 4;
        }
        else
        {
            separatorIndex = rawHTTP.IndexOf(LfSeparator);
            if (separatorIndex < 0)
            {
                return null;
            }
            separatorLen = 2;
        }

        var headerBytes = rawHTTP[..separatorIndex];
        var bodyBytes = rawHTTP[(separatorIndex + separatorLen)..];

        var headerText = LossyUtf8.GetString(headerBytes);
        var newlineIdx = headerText.IndexOfAny(['\r', '\n']);
        var firstLine = (newlineIdx >= 0 ? headerText[..newlineIdx] : headerText).Trim();
        if (string.IsNullOrEmpty(firstLine))
        {
            return null;
        }

        var statusParts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2
            || !statusParts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(statusParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int statusCode)
            || statusCode is < 200 or >= 300)
        {
            return null;
        }

        return Parse(bodyBytes);
    }

    public static PortalQueryResult? Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        // Decode lossily as UTF-8 so raw GBK bytes inside fields such as "NID" never fail decoding.
        var text = LossyUtf8.GetString(data);
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        var jsonSlice = text.AsMemory(start, end - start + 1);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonSlice);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? result = null;
            string? uid = null;
            long? fee = null;
            ulong? olflow = null;
            ulong? flow = null;
            ulong? v6af = null;
            ulong? time = null;
            string? v4ip = null;
            string? v46ip = null;
            string? v6ip = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "result":
                        result = ReadFlexibleInt(prop.Value);
                        break;
                    case "uid":
                        uid = ReadOptionalString(prop.Value);
                        break;
                    case "fee":
                        fee = ReadOptionalInt64(prop.Value);
                        break;
                    case "olflow":
                        olflow = ReadOptionalUInt64(prop.Value);
                        break;
                    case "flow":
                        flow = ReadOptionalUInt64(prop.Value);
                        break;
                    case "v6af":
                        v6af = ReadOptionalUInt64(prop.Value);
                        break;
                    case "time":
                        time = ReadOptionalUInt64(prop.Value);
                        break;
                    case "v4ip":
                        v4ip = ReadOptionalString(prop.Value);
                        break;
                    case "v46ip":
                        v46ip = ReadOptionalString(prop.Value);
                        break;
                    case "v6ip":
                        v6ip = ReadOptionalString(prop.Value);
                        break;
                }
            }

            return result switch
            {
                0 => PortalQueryResult.Offline,
                1 => PortalQueryResult.Online(BuildQuota(uid, fee, olflow, flow, v6af, time, v4ip, v46ip, v6ip)),
                _ => null,
            };
        }
    }

    private static CampusQuota BuildQuota(
        string? uid,
        long? fee,
        ulong? olflow,
        ulong? flow,
        ulong? v6af,
        ulong? time,
        string? v4ip,
        string? v46ip,
        string? v6ip)
    {
        double? balanceYuan = fee is { } rawFee
            ? (rawFee >= 0 ? (rawFee - (rawFee % 100)) / 10_000.0 : rawFee / 10_000.0)
            : null;

        bool unlimited = olflow == uint.MaxValue;
        ulong? remainingKB = unlimited ? null : olflow;

        return new CampusQuota
        {
            AccountID = NonEmpty(uid),
            BalanceYuan = balanceYuan,
            RemainingKB = remainingKB,
            IsUnlimitedFlow = unlimited,
            UsedKB = flow,
            IPv6UsedKB = v6af,
            OnlineMinutes = time,
            IPv4Address = NonEmpty(v4ip) ?? NonEmpty(v46ip),
            IPv6Address = NonEmpty(v6ip),
        };
    }

    private static int? ReadFlexibleInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int intVal))
        {
            return intVal;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadOptionalString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static long? ReadOptionalInt64(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long value) ? value : null;

    private static ulong? ReadOptionalUInt64(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out ulong value) ? value : null;

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
