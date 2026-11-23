using System.Text;
using System.Text.Json;
using CAUCCampusCore;
using Xunit;

namespace CAUCCampusCore.Tests;

public sealed class CampusQuotaParserTests
{
    [Fact]
    public void ParsesRealCAUCPayloadWithDuplicateKeyAndMissingTrailingParenthesis()
    {
        const string raw = """ dr1002({"result":1,"time":33287,"flow":212418672,"fsele":1,"fee":259800,"m46":0,"v46ip":"10.0.0.100","myv6ip":"","oltime":4294967295,"olflow":53227520,"lip":"10.0.0.100","stime":"2026-11-21 19:47:48","etime":"2026-11-21 20:17:54","uid":"202400001","v6af":185273559,"v6df":148855911,"v46m":4,"v4ip":"10.0.0.100","v6ip":"2001:db8::1","AC":"202400001","ss5":"10.0.0.100","ss6":"192.168.100.200","vid":0,"ss1":"020000000002","ss4":"000000000000","cvid":0,"pvid":0,"hotel":0,"aolno":15701,"eport":-1,"eclass":1,"zxopt":1,"NID":"?????","olno":1,"udate":"","olmac":"020000000001","ollm":10,"olm1":"00000014","olm2":"4210","olm3":0,"olmm":1,"olm5":0,"gid":4,"ispid":0,"opip":"0.0.0.0","olno":1,"olip":"10.0.0.200","oaf":2855202,"oat":25636,"actM":1,"actt":9833,"actdf":2766037,"actuf":0,"act6df":76535,"act6uf":10112,"allfm":1,"d1":0,"u1":0,"d2":0,"u2":0,"o1":0,"nd1":11064151,"nu1":2875006,"nd2":306143,"nu2":40448,"no1":0}""";
        var parsed = CampusQuotaParser.Parse(Encoding.UTF8.GetBytes(raw));

        Assert.NotNull(parsed);
        Assert.Equal(PortalQueryKind.Online, parsed!.Kind);
        var quota = Assert.IsType<CampusQuota>(parsed.Quota);

        Assert.Equal("202400001", quota.AccountID);
        Assert.Equal(25.98, quota.BalanceYuan);
        Assert.Equal("¥25.98", quota.BalanceText);
        Assert.Equal(53_227_520UL, quota.RemainingKB);
        Assert.False(quota.IsUnlimitedFlow);
        Assert.Equal("50.76 GB", quota.RemainingText);
        Assert.Equal(212_418_672UL, quota.UsedKB);
        Assert.Equal("202.58 GB", quota.UsedText);
        Assert.Equal(185_273_559UL, quota.IPv6UsedKB);
        Assert.Equal("176.69 GB", quota.IPv6UsedText);
        Assert.Equal("10.0.0.100", quota.IPv4Address);
        Assert.Equal("2001:db8::1", quota.IPv6Address);
    }

    [Fact]
    public void SurvivesInvalidUtf8OrRawGbkBytesInsideNidField()
    {
        var bytes = new List<byte>();
        bytes.AddRange("dr1002({\"result\":1,\"uid\":\"202400001\",\"fee\":125055,\"olflow\":1048576,\"NID\":\""u8);
        // Raw GBK bytes for "张三" (invalid UTF-8)
        bytes.AddRange([0xD5, 0xC5, 0xC8, 0xFD]);
        bytes.AddRange("\"});"u8);

        var parsed = CampusQuotaParser.Parse(bytes.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal(PortalQueryKind.Online, parsed!.Kind);
        var quota = parsed.Quota!;

        Assert.Equal("202400001", quota.AccountID);
        Assert.Equal(12.50, quota.BalanceYuan);
        Assert.Equal("¥12.50", quota.BalanceText);
        Assert.Equal(1_048_576UL, quota.RemainingKB);
        Assert.Equal("1.00 GB", quota.RemainingText);
    }

    [Fact]
    public void ParsesUnauthenticatedOfflineDrComStatus()
    {
        var raw = Encoding.UTF8.GetBytes("""dr1002({"result":0,"v46ip":"10.0.0.100"});""");
        var parsed = CampusQuotaParser.Parse(raw);
        Assert.Equal(PortalQueryResult.Offline, parsed);
    }

    [Fact]
    public void HandlesUInt32MaxOlflowAsUnlimitedFlow()
    {
        var raw = Encoding.UTF8.GetBytes("""dr1002({"result":1,"uid":"202400001","fee":50000,"olflow":4294967295});""");
        var parsed = CampusQuotaParser.Parse(raw);

        Assert.NotNull(parsed);
        Assert.Equal(PortalQueryKind.Online, parsed!.Kind);
        var quota = parsed.Quota!;

        Assert.True(quota.IsUnlimitedFlow);
        Assert.Null(quota.RemainingKB);
        Assert.Equal("不限量", quota.RemainingText);
        Assert.Equal("¥5.00", quota.BalanceText);
    }

    [Fact]
    public void ReturnsNullForMalformedOrNonJsonBodies()
    {
        Assert.Null(CampusQuotaParser.Parse("<html>not json</html>"u8));
        Assert.Null(CampusQuotaParser.Parse("dr1002({invalid})"u8));
        Assert.Null(CampusQuotaParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FormatsTrafficAcrossKbMbGbThresholdsAndCurrency()
    {
        Assert.Equal("512 KB", CampusQuota.FormatTraffic(512));
        Assert.Equal("2.0 MB", CampusQuota.FormatTraffic(2048));
        Assert.Equal("1.50 GB", CampusQuota.FormatTraffic(1_572_864));
        Assert.Equal("¥12.50", CampusQuota.FormatCurrency(12.5));
    }

    [Fact]
    public void ParsesRawHttp200ResponseContainingCaucJsonpPayload()
    {
        const string body = """dr1002({"result":1,"uid":"202400001","fee":259800,"olflow":53227520,"flow":212418672,"v6af":185273559});""";
        var rawHttp = $"HTTP/1.1 200 OK\r\nContent-Type: text/javascript; charset=gbk\r\nConnection: close\r\n\r\n{body}";
        var parsed = CampusQuotaParser.ParseHTTPResponse(Encoding.UTF8.GetBytes(rawHttp));

        Assert.NotNull(parsed);
        Assert.Equal(PortalQueryKind.Online, parsed!.Kind);
        var quota = parsed.Quota!;

        Assert.Equal("202400001", quota.AccountID);
        Assert.Equal("¥25.98", quota.BalanceText);
        Assert.Equal("50.76 GB", quota.RemainingText);
    }

    [Fact]
    public void RejectsNon2xxHttpStatusCodesInRawHttpResponses()
    {
        var redirectHttp = "HTTP/1.1 302 Found\r\nLocation: http://192.168.100.200/\r\n\r\n{\"result\":1}"u8;
        var notFoundHttp = "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\n\r\n{\"result\":1}"u8;

        Assert.Null(CampusQuotaParser.ParseHTTPResponse(redirectHttp));
        Assert.Null(CampusQuotaParser.ParseHTTPResponse(notFoundHttp));
    }

    [Fact]
    public void CachedCampusQuotaAndCampusQuotaRoundTripThroughJson()
    {
        var quota = new CampusQuota
        {
            AccountID = "202400001",
            BalanceYuan = 25.98,
            RemainingKB = 53_227_520,
            IsUnlimitedFlow = false,
            UsedKB = 212_418_672,
            IPv6UsedKB = 185_273_559,
            OnlineMinutes = 33_287,
            IPv4Address = "10.0.0.100",
            IPv6Address = "2001:db8::1",
        };
        var original = new CachedCampusQuota(quota, DateTimeOffset.FromUnixTimeSeconds(1_795_273_851));

        var json = JsonSerializer.Serialize(original);
        var decoded = JsonSerializer.Deserialize<CachedCampusQuota>(json);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ParsesRawHttpResponseUsingLfOnlyHeaderSeparator()
    {
        var rawHttp = "HTTP/1.1 200 OK\nContent-Type: text/javascript\n\ndr1002({\"result\":0});"u8;
        Assert.Equal(PortalQueryResult.Offline, CampusQuotaParser.ParseHTTPResponse(rawHttp));
    }
}

public sealed class QuotaFetchCoordinatorTests
{
    [Fact]
    public async Task SmartReusesOnlineWithinFreshTtlAndRefetchesAfterExpiry()
    {
        var q1 = new CampusQuota { AccountID = "202400001", BalanceYuan = 25.0, RemainingKB = 1_048_576 };
        var q2 = new CampusQuota { AccountID = "202400001", BalanceYuan = 24.5, RemainingKB = 1_000_000 };
        var recorder = new QuotaFetchRecorder([PortalQueryResult.Online(q1), PortalQueryResult.Online(q2)]);
        var coordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: _ => Task.FromResult(recorder.Fetch())
        );

        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.Equal(PortalQueryResult.Online(q1), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0));
        Assert.Equal(1, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(q1), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(59)));
        Assert.Equal(1, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(q2), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(60)));
        Assert.Equal(2, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(q2), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(90)));
        Assert.Equal(2, recorder.CallCount);
    }

    [Fact]
    public async Task SmartSuppressesUnreachableWithinCooldownAndRefetchesOnPathChangeOrForce()
    {
        var qOnline = new CampusQuota { AccountID = "202400001", BalanceYuan = 10.0, RemainingKB = 524_288 };
        var qUpdated = new CampusQuota { AccountID = "202400001", BalanceYuan = 9.5, RemainingKB = 500_000 };
        var recorder = new QuotaFetchRecorder([
            PortalQueryResult.Unreachable,
            PortalQueryResult.Unreachable,
            PortalQueryResult.Online(qOnline),
            PortalQueryResult.Online(qUpdated),
        ]);
        var coordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: _ => Task.FromResult(recorder.Fetch())
        );

        var t0 = DateTimeOffset.FromUnixTimeSeconds(2_000_000);
        Assert.Equal(PortalQueryResult.Unreachable, await coordinator.FetchAsync(QuotaFetchMode.Smart("tun:utun4|wifi:en0"), t0));
        Assert.Equal(1, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Unreachable, await coordinator.FetchAsync(QuotaFetchMode.Smart("tun:utun4|wifi:en0"), t0.AddSeconds(30)));
        Assert.Equal(1, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Unreachable, await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(31)));
        Assert.Equal(2, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(qOnline), await coordinator.FetchAsync(QuotaFetchMode.Force("wifi:en0"), t0.AddSeconds(35)));
        Assert.Equal(3, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(qUpdated), await coordinator.FetchAsync(QuotaFetchMode.Force("wifi:en0"), t0.AddSeconds(40)));
        Assert.Equal(4, recorder.CallCount);
    }

    [Fact]
    public async Task SmartDoesNotSuppressOfflineRechecks()
    {
        var qOnline = new CampusQuota { AccountID = "202400001", BalanceYuan = 15.0, RemainingKB = 2_097_152 };
        var recorder = new QuotaFetchRecorder([
            PortalQueryResult.Offline,
            PortalQueryResult.Offline,
            PortalQueryResult.Online(qOnline),
        ]);
        var coordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: _ => Task.FromResult(recorder.Fetch())
        );

        var t0 = DateTimeOffset.FromUnixTimeSeconds(3_000_000);
        Assert.Equal(PortalQueryResult.Offline, await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0));
        Assert.Equal(1, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Offline, await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(1)));
        Assert.Equal(2, recorder.CallCount);

        Assert.Equal(PortalQueryResult.Online(qOnline), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(2)));
        Assert.Equal(3, recorder.CallCount);
    }

    [Fact]
    public async Task InvalidateClearsStoredAndInFlightStateWithoutDeadlock()
    {
        var q1 = new CampusQuota { AccountID = "202400001", BalanceYuan = 20.0, RemainingKB = 1_048_576 };
        var q2 = new CampusQuota { AccountID = "202400001", BalanceYuan = 19.0, RemainingKB = 1_000_000 };
        var recorder = new QuotaFetchRecorder([
            PortalQueryResult.Online(q1),
            PortalQueryResult.Unreachable,
            PortalQueryResult.Online(q2),
        ]);
        var coordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: _ => Task.FromResult(recorder.Fetch())
        );

        var t0 = DateTimeOffset.FromUnixTimeSeconds(4_000_000);
        Assert.Equal(PortalQueryResult.Online(q1), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0));
        Assert.Equal(1, recorder.CallCount);

        coordinator.Invalidate();
        Assert.Equal(PortalQueryResult.Unreachable, await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(1)));
        Assert.Equal(2, recorder.CallCount);

        coordinator.Invalidate();
        Assert.Equal(PortalQueryResult.Online(q2), await coordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(2)));
        Assert.Equal(3, recorder.CallCount);

        // Invalidate during an in-flight fetch prevents stale caching and never deadlocks
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRecorder = new QuotaFetchRecorder([PortalQueryResult.Online(q1), PortalQueryResult.Online(q2)]);
        var gatedCoordinator = new QuotaFetchCoordinator(
            freshTTL: TimeSpan.FromSeconds(60),
            unreachableCooldown: TimeSpan.FromSeconds(60),
            fetcher: async _ =>
            {
                var res = gatedRecorder.Fetch();
                if (res == PortalQueryResult.Online(q1))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return res;
            }
        );

        var firstTask = gatedCoordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0);
        await entered.Task;
        gatedCoordinator.Invalidate();
        release.TrySetResult();

        Assert.Equal(PortalQueryResult.Online(q1), await firstTask);
        Assert.Equal(PortalQueryResult.Online(q2), await gatedCoordinator.FetchAsync(QuotaFetchMode.Smart("wifi:en0"), t0.AddSeconds(1)));
        Assert.Equal(2, gatedRecorder.CallCount);
    }

    private sealed class QuotaFetchRecorder(PortalQueryResult[] results)
    {
        private readonly object _lock = new();
        private int _index;
        private int _callCount;

        public int CallCount
        {
            get
            {
                lock (_lock)
                {
                    return _callCount;
                }
            }
        }

        public PortalQueryResult Fetch()
        {
            lock (_lock)
            {
                _callCount++;
                if (results.Length == 0)
                {
                    return PortalQueryResult.Unreachable;
                }

                var value = results[Math.Min(_index, results.Length - 1)];
                _index++;
                return value;
            }
        }
    }
}
