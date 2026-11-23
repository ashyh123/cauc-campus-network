import Foundation

public enum CampusEndpoints {
    public static let portalURL = URL(string: "http://192.168.100.200/")!
    public static let chkstatusBase = "http://192.168.100.200/drcom/chkstatus"
    public static let ipv6TriggerURL = URL(string: "http://test6.ustc.edu.cn/")!
    public static let ustcIPv6Probe = URL(string: "https://test6.ustc.edu.cn/")!
    public static let publicTargets = [
        URL(string: "https://www.cloudflare.com/cdn-cgi/trace")!,
        URL(string: "https://www.apple.com/library/test/success.html")!,
    ]
}

public struct ConnectivityStatus: Equatable, Sendable {
    public var ipv4: Bool
    public var ipv6: Bool

    public var isDualStackOnline: Bool {
        ipv4 && ipv6
    }

    public init(ipv4: Bool = false, ipv6: Bool = false) {
        self.ipv4 = ipv4
        self.ipv6 = ipv6
    }
}

public struct CampusQuota: Codable, Equatable, Sendable {
    public let accountID: String?
    public let balanceYuan: Double?
    public let remainingKB: UInt64?
    public let isUnlimitedFlow: Bool
    public let usedKB: UInt64?
    public let ipv6UsedKB: UInt64?
    public let onlineMinutes: UInt64?
    public let ipv4Address: String?
    public let ipv6Address: String?

    public init(
        accountID: String? = nil,
        balanceYuan: Double? = nil,
        remainingKB: UInt64? = nil,
        isUnlimitedFlow: Bool = false,
        usedKB: UInt64? = nil,
        ipv6UsedKB: UInt64? = nil,
        onlineMinutes: UInt64? = nil,
        ipv4Address: String? = nil,
        ipv6Address: String? = nil
    ) {
        self.accountID = accountID
        self.balanceYuan = balanceYuan
        self.remainingKB = remainingKB
        self.isUnlimitedFlow = isUnlimitedFlow
        self.usedKB = usedKB
        self.ipv6UsedKB = ipv6UsedKB
        self.onlineMinutes = onlineMinutes
        self.ipv4Address = ipv4Address
        self.ipv6Address = ipv6Address
    }

    public var remainingText: String {
        if isUnlimitedFlow {
            return "不限量"
        }
        guard let remainingKB else {
            return "--"
        }
        return Self.formatTraffic(kb: remainingKB)
    }

    public var balanceText: String {
        guard let balanceYuan else {
            return "--"
        }
        return Self.formatCurrency(balanceYuan)
    }

    public var usedText: String? {
        usedKB.map(Self.formatTraffic)
    }

    public var ipv6UsedText: String? {
        ipv6UsedKB.map(Self.formatTraffic)
    }

    public var isLowRemainingFlow: Bool {
        remainingKB.map { $0 < 1_048_576 } ?? false
    }

    public var isLowBalance: Bool {
        balanceYuan.map { $0 < 5.0 } ?? false
    }

    public static func formatTraffic(kb: UInt64) -> String {
        if kb < 1024 {
            return "\(kb) KB"
        }
        if kb < 1_048_576 {
            return String(format: "%.1f MB", Double(kb) / 1024.0)
        }
        return String(format: "%.2f GB", Double(kb) / 1_048_576.0)
    }

    public static func formatCurrency(_ yuan: Double) -> String {
        String(format: "¥%.2f", yuan)
    }
}

public struct CachedCampusQuota: Codable, Equatable, Sendable {
    public let quota: CampusQuota
    public let updatedAt: Date

    public init(quota: CampusQuota, updatedAt: Date) {
        self.quota = quota
        self.updatedAt = updatedAt
    }
}

public enum PortalQueryResult: Equatable, Sendable {
    case online(CampusQuota)
    case offline
    case unreachable
}
