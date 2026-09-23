import Foundation

struct TunnelSupervisorOptions {
    let initialBackoff: TimeInterval
    let maxBackoff: TimeInterval
    let restartWindow: TimeInterval
    let maxRestartsInWindow: Int
    let stableRunReset: TimeInterval
    let jitterRatio: Double

    static let `default` = TunnelSupervisorOptions(
        initialBackoff: 1,
        maxBackoff: 30,
        restartWindow: 5 * 60,
        maxRestartsInWindow: 5,
        stableRunReset: 2 * 60,
        jitterRatio: 0.20
    )

    init(
        initialBackoff: TimeInterval,
        maxBackoff: TimeInterval,
        restartWindow: TimeInterval,
        maxRestartsInWindow: Int,
        stableRunReset: TimeInterval,
        jitterRatio: Double
    ) {
        precondition(initialBackoff > 0, "initialBackoff must be positive")
        precondition(maxBackoff >= initialBackoff, "maxBackoff must be >= initialBackoff")
        precondition(restartWindow > 0, "restartWindow must be positive")
        precondition(maxRestartsInWindow > 0, "maxRestartsInWindow must be positive")
        precondition(stableRunReset > 0, "stableRunReset must be positive")
        precondition((0...1).contains(jitterRatio), "jitterRatio must be between 0 and 1")
        self.initialBackoff = initialBackoff
        self.maxBackoff = maxBackoff
        self.restartWindow = restartWindow
        self.maxRestartsInWindow = maxRestartsInWindow
        self.stableRunReset = stableRunReset
        self.jitterRatio = jitterRatio
    }
}

struct TunnelRestartDecision {
    let isCooldown: Bool
    let delay: TimeInterval
    let attemptNumber: Int
    let resumeAt: Date?
}

final class TunnelRestartPolicy {
    private let options: TunnelSupervisorOptions
    private var attempts: [Date] = []
    private(set) var consecutiveRestarts = 0

    init(options: TunnelSupervisorOptions = .default) {
        self.options = options
    }

    var attemptsInWindow: Int { attempts.count }

    func next(
        processStarted: Date,
        now: Date,
        random: () -> Double = { Double.random(in: 0...1) }
    ) -> TunnelRestartDecision {
        if now.timeIntervalSince(processStarted) >= options.stableRunReset {
            reset()
        }

        attempts.removeAll { now.timeIntervalSince($0) >= options.restartWindow }

        if attempts.count >= options.maxRestartsInWindow {
            let resumeAt = attempts[0].addingTimeInterval(options.restartWindow)
            let delay = max(0, resumeAt.timeIntervalSince(now))
            return TunnelRestartDecision(
                isCooldown: true,
                delay: delay,
                attemptNumber: consecutiveRestarts,
                resumeAt: resumeAt
            )
        }

        let exponent = min(consecutiveRestarts, 30)
        let baseDelay = options.initialBackoff * pow(2, Double(exponent))
        let clamped = min(baseDelay, options.maxBackoff)
        let sample = min(1, max(0, random()))
        let jitterFactor = 1 - options.jitterRatio + (2 * options.jitterRatio * sample)
        let delay = min(options.maxBackoff, max(0, clamped * jitterFactor))

        attempts.append(now)
        consecutiveRestarts += 1
        return TunnelRestartDecision(
            isCooldown: false,
            delay: delay,
            attemptNumber: consecutiveRestarts,
            resumeAt: nil
        )
    }

    func reset() {
        attempts.removeAll(keepingCapacity: true)
        consecutiveRestarts = 0
    }
}
