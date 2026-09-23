import Foundation
import Security
import CryptoKit

struct LogicalChatConnection {
    let chatInstanceID: String
    let resumed: Bool
}

struct LogicalChatCorrelationRetentionSnapshot {
    let knownHandles: Int
    let expiredEvictions: Int64
    let pressureEvictions: Int64
    let capacityPressureEvents: Int64
}

enum LogicalChatCorrelationError: LocalizedError {
    case invalidHandle
    case unknownHandle
    case randomFailure(OSStatus)

    var errorDescription: String? {
        switch self {
        case .invalidHandle:
            return "Invalid FileMCP chat correlation handle."
        case .unknownHandle:
            return "Unknown FileMCP chat correlation handle. Call filemcp_observability_connect without chat_instance_id to establish a new correlation handle."
        case let .randomFailure(status):
            return "Could not generate a FileMCP chat correlation handle (OSStatus \(status))."
        }
    }
}

final class LogicalChatCorrelationService {
    static let handlePrefix = "chat_"
    static let defaultMaxKnownHandles = 4096
    static let defaultRetention: TimeInterval = 2 * 60 * 60
    private static let randomByteCount = 32

    private struct Entry {
        var lastSeen: Date
    }

    private let lock = NSLock()
    private let maxKnownHandles: Int
    private let retention: TimeInterval
    private let nowProvider: () -> Date
    private let randomBytesProvider: (Int) throws -> [UInt8]

    private var knownHashes: [String: Entry] = [:]
    private var expiredEvictions: Int64 = 0
    private var pressureEvictions: Int64 = 0
    private var capacityPressureEvents: Int64 = 0

    init(
        maxKnownHandles: Int = defaultMaxKnownHandles,
        retention: TimeInterval = defaultRetention,
        nowProvider: @escaping () -> Date = Date.init,
        randomBytesProvider: @escaping (Int) throws -> [UInt8] = LogicalChatCorrelationService.secureRandomBytes
    ) {
        precondition(maxKnownHandles > 0, "maxKnownHandles must be positive")
        precondition(retention > 0, "retention must be positive")
        self.maxKnownHandles = maxKnownHandles
        self.retention = retention
        self.nowProvider = nowProvider
        self.randomBytesProvider = randomBytesProvider
    }

    func connect(chatInstanceID: String? = nil, now: Date? = nil) throws -> LogicalChatConnection {
        let timestamp = now ?? nowProvider()
        lock.lock()
        defer { lock.unlock() }

        removeExpiredLocked(now: timestamp)

        if let requested = chatInstanceID?.trimmingCharacters(in: .whitespacesAndNewlines),
           !requested.isEmpty {
            guard Self.isValidHandle(requested) else {
                throw LogicalChatCorrelationError.invalidHandle
            }
            let hash = Self.hash(requested)
            guard var entry = knownHashes[hash] else {
                throw LogicalChatCorrelationError.unknownHandle
            }
            if timestamp > entry.lastSeen {
                entry.lastSeen = timestamp
                knownHashes[hash] = entry
            }
            return LogicalChatConnection(chatInstanceID: requested, resumed: true)
        }

        ensureCapacityForOneLocked(now: timestamp)
        while true {
            let randomBytes = try randomBytesProvider(Self.randomByteCount)
            guard randomBytes.count == Self.randomByteCount else {
                throw LogicalChatCorrelationError.randomFailure(errSecParam)
            }
            let handle = Self.handlePrefix + Self.base64URL(randomBytes)
            let hash = Self.hash(handle)
            if knownHashes[hash] == nil {
                knownHashes[hash] = Entry(lastSeen: timestamp)
                return LogicalChatConnection(chatInstanceID: handle, resumed: false)
            }
        }
    }

    func tryResolve(_ chatInstanceID: String?, now: Date? = nil) -> String? {
        guard let candidate = chatInstanceID?.trimmingCharacters(in: .whitespacesAndNewlines),
              !candidate.isEmpty,
              Self.isValidHandle(candidate) else {
            return nil
        }

        let timestamp = now ?? nowProvider()
        lock.lock()
        defer { lock.unlock() }

        removeExpiredLocked(now: timestamp)
        let hash = Self.hash(candidate)
        guard var entry = knownHashes[hash] else { return nil }
        if timestamp > entry.lastSeen {
            entry.lastSeen = timestamp
            knownHashes[hash] = entry
        }
        return hash
    }

    func cleanup(now: Date? = nil) -> LogicalChatCorrelationRetentionSnapshot {
        let timestamp = now ?? nowProvider()
        lock.lock()
        defer { lock.unlock() }
        removeExpiredLocked(now: timestamp)
        trimToCountLocked(maxKnownHandles, pressure: true)
        return retentionSnapshotLocked()
    }

    func retentionSnapshot() -> LogicalChatCorrelationRetentionSnapshot {
        lock.lock()
        defer { lock.unlock() }
        return retentionSnapshotLocked()
    }

    static func isValidHandle(_ value: String) -> Bool {
        guard value.hasPrefix(handlePrefix),
              value.count == handlePrefix.count + 43 else {
            return false
        }
        return value.dropFirst(handlePrefix.count).utf8.allSatisfy { byte in
            (byte >= 48 && byte <= 57) ||
            (byte >= 65 && byte <= 90) ||
            (byte >= 97 && byte <= 122) ||
            byte == 45 || byte == 95
        }
    }

    static func hashForPersistence(_ chatInstanceID: String) throws -> String {
        guard isValidHandle(chatInstanceID) else {
            throw LogicalChatCorrelationError.invalidHandle
        }
        return hash(chatInstanceID)
    }

    private func ensureCapacityForOneLocked(now: Date) {
        if knownHashes.count < maxKnownHandles { return }
        capacityPressureEvents += 1
        removeExpiredLocked(now: now)
        if knownHashes.count >= maxKnownHandles {
            trimToCountLocked(maxKnownHandles - 1, pressure: true)
        }
    }

    private func removeExpiredLocked(now: Date) {
        let cutoff = now.addingTimeInterval(-retention)
        let expired = knownHashes.compactMap { key, entry in
            entry.lastSeen <= cutoff ? key : nil
        }
        for key in expired {
            if knownHashes.removeValue(forKey: key) != nil {
                expiredEvictions += 1
            }
        }
    }

    private func trimToCountLocked(_ targetCount: Int, pressure: Bool) {
        let target = max(0, targetCount)
        guard knownHashes.count > target else { return }
        let ordered = knownHashes.sorted { lhs, rhs in
            lhs.value.lastSeen < rhs.value.lastSeen
        }
        for pair in ordered {
            if knownHashes.count <= target { break }
            if knownHashes.removeValue(forKey: pair.key) != nil, pressure {
                pressureEvictions += 1
            }
        }
    }

    private func retentionSnapshotLocked() -> LogicalChatCorrelationRetentionSnapshot {
        LogicalChatCorrelationRetentionSnapshot(
            knownHandles: knownHashes.count,
            expiredEvictions: expiredEvictions,
            pressureEvictions: pressureEvictions,
            capacityPressureEvents: capacityPressureEvents
        )
    }

    private static func secureRandomBytes(count: Int) throws -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: count)
        let status = bytes.withUnsafeMutableBytes { buffer -> OSStatus in
            guard let baseAddress = buffer.baseAddress else { return errSecParam }
            return SecRandomCopyBytes(kSecRandomDefault, count, baseAddress)
        }
        guard status == errSecSuccess else {
            throw LogicalChatCorrelationError.randomFailure(status)
        }
        return bytes
    }

    private static func hash(_ value: String) -> String {
        let digest = SHA256.hash(data: Data(value.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private static func base64URL(_ bytes: [UInt8]) -> String {
        Data(bytes)
            .base64EncodedString()
            .trimmingCharacters(in: CharacterSet(charactersIn: "="))
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }
}
