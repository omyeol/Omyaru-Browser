import SwiftUI
import LocalAuthentication
import CryptoKit
import CommonCrypto
import UniformTypeIdentifiers
import UIKit
import CFNetwork
import Network

// MARK: - App Lock Manager (PIN 4~6 digits OR Password + Face ID / Touch ID + App Switcher Black Shield)

enum AppLockKind: String, CaseIterable, Codable, Identifiable {
    case pin = "PIN"
    case password = "PASSWORD"
    var id: String { rawValue }
    var title: String { self == .pin ? "숫자 PIN (4~6자리)" : "문자 비밀번호" }
}

@MainActor
final class AppLockManager: ObservableObject {
    static let shared = AppLockManager()

    @Published var isEnabled: Bool {
        didSet { UserDefaults.standard.set(isEnabled, forKey: "omyaru.ios.lock.enabled") }
    }
    @Published var lockKind: AppLockKind {
        didSet { UserDefaults.standard.set(lockKind.rawValue, forKey: "omyaru.ios.lock.kind") }
    }
    @Published var pinLength: Int {
        didSet { UserDefaults.standard.set(pinLength, forKey: "omyaru.ios.lock.pinLength") }
    }
    @Published var useBiometrics: Bool {
        didSet { UserDefaults.standard.set(useBiometrics, forKey: "omyaru.ios.lock.biometrics") }
    }
    @Published var lockOnBackground: Bool {
        didSet { UserDefaults.standard.set(lockOnBackground, forKey: "omyaru.ios.lock.onBackground") }
    }
    @Published var lockOnDeviceLock: Bool {
        didSet { UserDefaults.standard.set(lockOnDeviceLock, forKey: "omyaru.ios.lock.onDeviceLock") }
    }
    @Published var appSwitchBlackShield: Bool {
        didSet { UserDefaults.standard.set(appSwitchBlackShield, forKey: "omyaru.ios.lock.blackShield") }
    }
    @Published var isLocked: Bool = false
    @Published var isAuthenticatingBiometrics: Bool = false
    @Published var isBiometricUnlockGrace: Bool = false
    @Published var errorMessage: String = ""

    private var deviceLockObserver: NSObjectProtocol? = nil
    private var biometricGraceTask: DispatchWorkItem? = nil
    private let hashKey = "omyaru.ios.lock.pinHash"
    private let saltKey = "omyaru.ios.lock.pinSalt"

    var suppressInactiveShield: Bool {
        isAuthenticatingBiometrics || isBiometricUnlockGrace
    }

    private func beginBiometricGrace() {
        biometricGraceTask?.cancel()
        isBiometricUnlockGrace = true
        let task = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                self?.isBiometricUnlockGrace = false
            }
        }
        biometricGraceTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0, execute: task)
    }

    func clearBiometricGrace() {
        biometricGraceTask?.cancel()
        biometricGraceTask = nil
        if isBiometricUnlockGrace {
            isBiometricUnlockGrace = false
        }
    }

    init() {
        let d = UserDefaults.standard
        let hasSavedSecret = !(d.string(forKey: "omyaru.ios.lock.pinHash") ?? "").isEmpty
        let enabled = d.bool(forKey: "omyaru.ios.lock.enabled") && hasSavedSecret
        isEnabled = enabled
        lockKind = AppLockKind(rawValue: d.string(forKey: "omyaru.ios.lock.kind") ?? "PIN") ?? .pin
        pinLength = min(6, max(4, d.object(forKey: "omyaru.ios.lock.pinLength") as? Int ?? 4))
        useBiometrics = d.object(forKey: "omyaru.ios.lock.biometrics") as? Bool ?? true
        lockOnBackground = d.object(forKey: "omyaru.ios.lock.onBackground") as? Bool ?? true
        lockOnDeviceLock = d.object(forKey: "omyaru.ios.lock.onDeviceLock") as? Bool ?? true
        appSwitchBlackShield = d.bool(forKey: "omyaru.ios.lock.blackShield")
        isLocked = enabled

        deviceLockObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.protectedDataWillBecomeUnavailableNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.onDeviceLocked()
            }
        }
    }

    var hasSecret: Bool {
        !(UserDefaults.standard.string(forKey: hashKey) ?? "").isEmpty
    }

    @discardableResult
    func trySetEnabled(_ enabled: Bool) -> Bool {
        if enabled && !hasSecret {
            isEnabled = false
            errorMessage = "앱 잠금을 켜려면 먼저 PIN(4~6자리) 또는 비밀번호를 설정해야 합니다."
            return false
        }
        isEnabled = enabled
        if !enabled { isLocked = false }
        errorMessage = ""
        return true
    }

    func setSecret(_ secret: String, kind: AppLockKind) -> Bool {
        let trimmed = secret.trimmingCharacters(in: .whitespacesAndNewlines)
        if kind == .pin {
            guard (4...6).contains(trimmed.count), trimmed.allSatisfy(\.isNumber) else {
                errorMessage = "PIN은 4~6자리 숫자여야 합니다."
                return false
            }
            pinLength = trimmed.count
        } else {
            guard trimmed.count >= 4, trimmed.count <= 64 else {
                errorMessage = "비밀번호는 4~64자여야 합니다."
                return false
            }
        }
        var saltBytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, 16, &saltBytes)
        let salt = Data(saltBytes)
        let hash = Self.hashSecret(trimmed, salt: salt)
        UserDefaults.standard.set(salt.base64EncodedString(), forKey: saltKey)
        UserDefaults.standard.set(hash.base64EncodedString(), forKey: hashKey)
        lockKind = kind
        isEnabled = true
        isLocked = false
        errorMessage = ""
        return true
    }

    func verifySecret(_ input: String) -> Bool {
        guard let saltB64 = UserDefaults.standard.string(forKey: saltKey),
              let salt = Data(base64Encoded: saltB64),
              let expectedB64 = UserDefaults.standard.string(forKey: hashKey),
              let expected = Data(base64Encoded: expectedB64) else {
            return false
        }
        let actual = Self.hashSecret(input.trimmingCharacters(in: .whitespacesAndNewlines), salt: salt)
        if actual == expected {
            isLocked = false
            errorMessage = ""
            return true
        } else {
            errorMessage = lockKind == .pin ? "PIN이 일치하지 않습니다." : "비밀번호가 일치하지 않습니다."
            return false
        }
    }

    func disableLock() {
        isEnabled = false
        isLocked = false
        UserDefaults.standard.removeObject(forKey: hashKey)
        UserDefaults.standard.removeObject(forKey: saltKey)
    }

    func lockIfNeeded() {
        clearBiometricGrace()
        if isEnabled && hasSecret && lockOnBackground {
            isLocked = true
        }
    }

    func onDeviceLocked() {
        clearBiometricGrace()
        if isEnabled && hasSecret && lockOnDeviceLock {
            isLocked = true
        }
    }

    func unlockWithBiometrics() async {
        guard isEnabled, useBiometrics, !isAuthenticatingBiometrics else { return }
        let context = LAContext()
        var error: NSError?
        guard context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) else {
            return
        }
        isAuthenticatingBiometrics = true
        defer { isAuthenticatingBiometrics = false }
        do {
            let success = try await context.evaluatePolicy(
                .deviceOwnerAuthenticationWithBiometrics,
                localizedReason: "Omyaru Browser 잠금 해제"
            )
            if success {
                beginBiometricGrace()
                var tx = Transaction()
                tx.disablesAnimations = true
                withTransaction(tx) {
                    isLocked = false
                    errorMessage = ""
                }
            }
        } catch {
            errorMessage = "생체 인증을 완료하지 못했습니다. 암호를 입력해 주세요."
        }
    }

    private static func hashSecret(_ secret: String, salt: Data) -> Data {
        var derived = [UInt8](repeating: 0, count: 32)
        _ = salt.withUnsafeBytes { saltBuf in
            secret.withCString { ptr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    ptr,
                    secret.utf8.count,
                    saltBuf.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    120_000,
                    &derived,
                    32
                )
            }
        }
        return Data(derived)
    }
}

struct AppLockOverlayView: View {
    @ObservedObject var lock: AppLockManager = .shared
    @State private var secretInput: String = ""

    var body: some View {
        ZStack {
            Color(red: 0.05, green: 0.05, blue: 0.06).ignoresSafeArea()
            VStack(spacing: 20) {
                Image(systemName: "lock.shield.fill")
                    .font(.system(size: 56))
                    .foregroundStyle(.blue)
                Text("Omyaru 잠금")
                    .font(.title2.weight(.bold))
                Text("브라우저와 책장을 보호하고 있습니다.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                SecureField(lock.lockKind == .pin ? "\(lock.pinLength)자리 PIN 입력" : "비밀번호 입력", text: $secretInput)
                    .keyboardType(lock.lockKind == .pin ? .numberPad : .default)
                    .multilineTextAlignment(.center)
                    .font(.title3.monospacedDigit())
                    .padding(12)
                    .frame(width: 240)
                    .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 12))
                    .onSubmit {
                        if lock.verifySecret(secretInput) { secretInput = "" }
                    }

                if !lock.errorMessage.isEmpty {
                    Text(lock.errorMessage)
                        .font(.caption)
                        .foregroundStyle(.red)
                }

                HStack(spacing: 12) {
                    Button {
                        if lock.verifySecret(secretInput) { secretInput = "" }
                    } label: {
                        Label("잠금 해제", systemImage: "key.fill")
                            .padding(.horizontal, 16)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.borderedProminent)

                    if lock.useBiometrics {
                        Button {
                            Task { await lock.unlockWithBiometrics() }
                        } label: {
                            Label("Face ID / Touch ID", systemImage: "faceid")
                                .padding(.horizontal, 16)
                                .padding(.vertical, 10)
                        }
                        .buttonStyle(.bordered)
                    }
                }
            }
            .padding(28)
        }
        .task {
            await lock.unlockWithBiometrics()
        }
    }
}

private struct LockSecretSetupSheet: View {
    @Environment(\.dismiss) private var dismiss
    let title: String
    let initialKind: AppLockKind
    let onSave: (String, AppLockKind) -> String?

    @State private var chosenKind: AppLockKind = .pin
    @State private var newSecret: String = ""
    @State private var confirmSecret: String = ""
    @State private var errorMessage: String = ""

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("잠금 방식", selection: $chosenKind) {
                        ForEach(AppLockKind.allCases) { kind in
                            Text(kind.title).tag(kind)
                        }
                    }
                    .pickerStyle(.segmented)

                    SecureField(chosenKind == .pin ? "새 PIN (4~6자리 숫자)" : "새 비밀번호 (4자 이상)", text: $newSecret)
                        .keyboardType(chosenKind == .pin ? .numberPad : .default)
                    SecureField("암호 확인", text: $confirmSecret)
                        .keyboardType(chosenKind == .pin ? .numberPad : .default)

                    if !errorMessage.isEmpty {
                        Text(errorMessage)
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                } header: {
                    Text("PIN / 비밀번호 설정")
                } footer: {
                    Text(chosenKind == .pin ? "4~6자리 숫자 PIN을 입력해 주세요." : "4자 이상 64자 이하의 비밀번호를 입력해 주세요.")
                }
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { chosenKind = initialKind }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("저장") {
                        guard newSecret == confirmSecret else {
                            errorMessage = "암호 확인이 일치하지 않습니다."
                            return
                        }
                        if let err = onSave(newSecret, chosenKind) {
                            errorMessage = err
                        } else {
                            dismiss()
                        }
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct AppLockSettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var lock: AppLockManager = .shared
    @State private var showSecretSetup = false

    private var enabledBinding: Binding<Bool> {
        Binding(
            get: { lock.isEnabled && lock.hasSecret },
            set: { next in
                if next {
                    showSecretSetup = true
                } else {
                    lock.disableLock()
                }
            }
        )
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("앱 잠금 사용", isOn: enabledBinding)
                    if lock.isEnabled && lock.hasSecret {
                        LabeledContent("잠금 방식") {
                            Text(lock.lockKind == .pin ? "\(lock.pinLength)자리 PIN" : "비밀번호")
                                .foregroundStyle(.green)
                        }
                        Toggle("Face ID / Touch ID 사용", isOn: $lock.useBiometrics)
                        Toggle("아이폰이 잠기면 같이 잠금", isOn: $lock.lockOnDeviceLock)
                        Toggle("백그라운드 전환 시 즉시 잠금", isOn: $lock.lockOnBackground)
                        Button("PIN / 비밀번호 변경...") {
                            showSecretSetup = true
                        }
                    }
                } header: {
                    Text("앱 잠금")
                } footer: {
                    Text("앱 잠금 사용을 켜면 PIN(4~6자리) 또는 비밀번호 설정 창이 열립니다. ‘아이폰이 잠기면 같이 잠금’을 켜면 기기 화면이 잠길 때 앱도 함께 잠깁니다.")
                }

                Section("화면 보호") {
                    Toggle("앱 전환기 검은 화면 보호 (미리보기 가리기)", isOn: $lock.appSwitchBlackShield)
                }
            }
            .navigationTitle("앱 잠금 설정")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .sheet(isPresented: $showSecretSetup) {
                LockSecretSetupSheet(
                    title: lock.hasSecret ? "앱 잠금 암호 변경" : "앱 잠금 암호 설정",
                    initialKind: lock.lockKind
                ) { secret, kind in
                    lock.setSecret(secret, kind: kind) ? nil : lock.errorMessage
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - Dedicated Bookshelf Lock (Timeout + iPhone Device Lock + Face ID / Custom PIN & Password)

enum BookshelfLockTimeout: Int, CaseIterable, Identifiable {
    case immediately = 0
    case thirtySeconds = 30
    case oneMinute = 60
    case threeMinutes = 180
    case fiveMinutes = 300
    case tenMinutes = 600
    case thirtyMinutes = 1800
    case oneHour = 3600
    case never = -1

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .immediately: return "즉시 (책장 닫을 때)"
        case .thirtySeconds: return "30초 후"
        case .oneMinute: return "1분 후"
        case .threeMinutes: return "3분 후"
        case .fiveMinutes: return "5분 후"
        case .tenMinutes: return "10분 후"
        case .thirtyMinutes: return "30분 후"
        case .oneHour: return "1시간 후"
        case .never: return "시간 잠금 안 함"
        }
    }
}

@MainActor
final class BookshelfLockManager: ObservableObject {
    static let shared = BookshelfLockManager()

    @Published var isEnabled: Bool {
        didSet {
            UserDefaults.standard.set(isEnabled, forKey: "omyaru.ios.bookshelfLock.enabled")
            if !isEnabled {
                isLocked = false
                lastInactiveAt = nil
            }
        }
    }
    @Published var lockKind: AppLockKind {
        didSet { UserDefaults.standard.set(lockKind.rawValue, forKey: "omyaru.ios.bookshelfLock.kind") }
    }
    @Published var pinLength: Int {
        didSet { UserDefaults.standard.set(pinLength, forKey: "omyaru.ios.bookshelfLock.pinLength") }
    }
    @Published var useBiometrics: Bool {
        didSet { UserDefaults.standard.set(useBiometrics, forKey: "omyaru.ios.bookshelfLock.biometrics") }
    }
    @Published var lockTimeout: BookshelfLockTimeout {
        didSet { UserDefaults.standard.set(lockTimeout.rawValue, forKey: "omyaru.ios.bookshelfLock.timeout") }
    }
    @Published var lockOnDeviceLock: Bool {
        didSet { UserDefaults.standard.set(lockOnDeviceLock, forKey: "omyaru.ios.bookshelfLock.onDeviceLock") }
    }
    @Published var lockOnBackground: Bool {
        didSet { UserDefaults.standard.set(lockOnBackground, forKey: "omyaru.ios.bookshelfLock.onBackground") }
    }
    @Published var isLocked: Bool = false
    @Published var isAuthenticatingBiometrics: Bool = false
    @Published var isBiometricUnlockGrace: Bool = false
    @Published var errorMessage: String = ""

    private var lastInactiveAt: Date? = nil
    private var deviceLockObserver: NSObjectProtocol? = nil
    private var biometricGraceTask: DispatchWorkItem? = nil
    private let hashKey = "omyaru.ios.bookshelfLock.pinHash"
    private let saltKey = "omyaru.ios.bookshelfLock.pinSalt"

    var suppressInactiveShield: Bool {
        isAuthenticatingBiometrics || isBiometricUnlockGrace
    }

    private func beginBiometricGrace() {
        biometricGraceTask?.cancel()
        isBiometricUnlockGrace = true
        let task = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                self?.isBiometricUnlockGrace = false
            }
        }
        biometricGraceTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0, execute: task)
    }

    func clearBiometricGrace() {
        biometricGraceTask?.cancel()
        biometricGraceTask = nil
        if isBiometricUnlockGrace {
            isBiometricUnlockGrace = false
        }
    }

    init() {
        let d = UserDefaults.standard
        let hasSavedSecret = !(d.string(forKey: "omyaru.ios.bookshelfLock.pinHash") ?? "").isEmpty
        let enabled = d.bool(forKey: "omyaru.ios.bookshelfLock.enabled") && hasSavedSecret
        isEnabled = enabled
        lockKind = AppLockKind(rawValue: d.string(forKey: "omyaru.ios.bookshelfLock.kind") ?? "PIN") ?? .pin
        pinLength = min(6, max(4, d.object(forKey: "omyaru.ios.bookshelfLock.pinLength") as? Int ?? 4))
        useBiometrics = d.object(forKey: "omyaru.ios.bookshelfLock.biometrics") as? Bool ?? true
        let savedTimeout = d.object(forKey: "omyaru.ios.bookshelfLock.timeout") as? Int ?? BookshelfLockTimeout.immediately.rawValue
        lockTimeout = BookshelfLockTimeout(rawValue: savedTimeout) ?? .immediately
        lockOnDeviceLock = d.object(forKey: "omyaru.ios.bookshelfLock.onDeviceLock") as? Bool ?? true
        lockOnBackground = d.object(forKey: "omyaru.ios.bookshelfLock.onBackground") as? Bool ?? false
        isLocked = enabled

        deviceLockObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.protectedDataWillBecomeUnavailableNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.onDeviceLocked()
            }
        }
    }

    var hasSecret: Bool {
        !(UserDefaults.standard.string(forKey: hashKey) ?? "").isEmpty
    }

    @discardableResult
    func trySetEnabled(_ enabled: Bool) -> Bool {
        if enabled && !hasSecret {
            isEnabled = false
            errorMessage = "책장 잠금을 켜려면 먼저 PIN(4~6자리) 또는 비밀번호를 설정해야 합니다."
            return false
        }
        isEnabled = enabled
        if !enabled {
            isLocked = false
            lastInactiveAt = nil
        }
        errorMessage = ""
        return true
    }

    /// Synchronous read-only check so BookshelfView never flashes photos on frame 0 before `.onAppear`.
    var isCurrentlyLocked: Bool {
        guard isEnabled && hasSecret else { return false }
        if isLocked { return true }
        guard lockTimeout != .never, let last = lastInactiveAt else { return false }
        return lockTimeout == .immediately || Date().timeIntervalSince(last) >= Double(lockTimeout.rawValue)
    }

    var summaryDescription: String {
        guard isEnabled && hasSecret else { return "꺼짐" }
        var parts: [String] = []
        parts.append(lockKind == .pin ? "\(pinLength)자리 PIN" : "비밀번호")
        if useBiometrics {
            parts.append("Face ID")
        }
        if lockTimeout != .never {
            parts.append(lockTimeout.title)
        }
        if lockOnDeviceLock {
            parts.append("아이폰 잠금 연동")
        }
        return parts.joined(separator: " · ")
    }

    func setSecret(_ secret: String, kind: AppLockKind) -> Bool {
        let trimmed = secret.trimmingCharacters(in: .whitespacesAndNewlines)
        if kind == .pin {
            guard (4...6).contains(trimmed.count), trimmed.allSatisfy(\.isNumber) else {
                errorMessage = "PIN은 4~6자리 숫자여야 합니다."
                return false
            }
            pinLength = trimmed.count
        } else {
            guard trimmed.count >= 4, trimmed.count <= 64 else {
                errorMessage = "비밀번호는 4~64자여야 합니다."
                return false
            }
        }
        var saltBytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, 16, &saltBytes)
        let salt = Data(saltBytes)
        let hash = Self.hashSecret(trimmed, salt: salt)
        UserDefaults.standard.set(salt.base64EncodedString(), forKey: saltKey)
        UserDefaults.standard.set(hash.base64EncodedString(), forKey: hashKey)
        lockKind = kind
        isEnabled = true
        isLocked = false
        lastInactiveAt = nil
        errorMessage = ""
        return true
    }

    func clearCustomSecret() {
        UserDefaults.standard.removeObject(forKey: hashKey)
        UserDefaults.standard.removeObject(forKey: saltKey)
        errorMessage = ""
    }

    func verifySecret(_ input: String) -> Bool {
        guard let saltB64 = UserDefaults.standard.string(forKey: saltKey),
              let salt = Data(base64Encoded: saltB64),
              let expectedB64 = UserDefaults.standard.string(forKey: hashKey),
              let expected = Data(base64Encoded: expectedB64) else {
            return false
        }
        let actual = Self.hashSecret(input.trimmingCharacters(in: .whitespacesAndNewlines), salt: salt)
        if actual == expected {
            isLocked = false
            lastInactiveAt = nil
            errorMessage = ""
            return true
        } else {
            errorMessage = lockKind == .pin ? "책장 PIN이 일치하지 않습니다." : "책장 비밀번호가 일치하지 않습니다."
            return false
        }
    }

    func disableLock() {
        isEnabled = false
        isLocked = false
        lastInactiveAt = nil
        clearCustomSecret()
    }

    func lockNow() {
        clearBiometricGrace()
        guard isEnabled else { return }
        isLocked = true
        lastInactiveAt = Date()
    }

    func onDeviceLocked() {
        clearBiometricGrace()
        guard isEnabled, lockOnDeviceLock else { return }
        isLocked = true
        lastInactiveAt = Date()
    }

    func onBookshelfOpened() {
        guard isEnabled else {
            isLocked = false
            return
        }
        evaluateTimeoutLockIfNeeded()
        if !isLocked {
            lastInactiveAt = nil
        }
    }

    func onBookshelfClosed() {
        clearBiometricGrace()
        guard isEnabled else { return }
        lastInactiveAt = Date()
        if lockTimeout == .immediately {
            isLocked = true
        }
    }

    func onAppBackgrounded() {
        clearBiometricGrace()
        guard isEnabled else { return }
        lastInactiveAt = Date()
        if lockOnBackground || lockTimeout == .immediately {
            isLocked = true
        }
    }

    func onAppForegrounded() {
        clearBiometricGrace()
        guard isEnabled else { return }
        evaluateTimeoutLockIfNeeded()
    }

    func evaluateTimeoutLockIfNeeded() {
        guard isEnabled, !isLocked else { return }
        guard lockTimeout != .never, let last = lastInactiveAt else { return }
        if lockTimeout == .immediately || Date().timeIntervalSince(last) >= Double(lockTimeout.rawValue) {
            isLocked = true
        }
    }

    func unlockWithDeviceAuth() async {
        guard isEnabled, !isAuthenticatingBiometrics else { return }
        let context = LAContext()
        var error: NSError?
        let policy: LAPolicy = hasSecret ? .deviceOwnerAuthenticationWithBiometrics : .deviceOwnerAuthentication
        if hasSecret && !useBiometrics { return }
        guard context.canEvaluatePolicy(policy, error: &error) else {
            if !hasSecret {
                errorMessage = "기기에 Face ID/Touch ID 또는 암호가 설정되어 있지 않습니다. 책장 전용 암호를 설정해 주세요."
            }
            return
        }
        isAuthenticatingBiometrics = true
        defer { isAuthenticatingBiometrics = false }
        do {
            let success = try await context.evaluatePolicy(
                policy,
                localizedReason: "Omyaru 책장 잠금 해제"
            )
            if success {
                beginBiometricGrace()
                var tx = Transaction()
                tx.disablesAnimations = true
                withTransaction(tx) {
                    isLocked = false
                    lastInactiveAt = nil
                    errorMessage = ""
                }
            }
        } catch {
            errorMessage = hasSecret
                ? "생체 인증을 완료하지 못했습니다. 책장 암호를 입력해 주세요."
                : "인증이 취소되었습니다. 다시 시도해 주세요."
        }
    }

    private static func hashSecret(_ secret: String, salt: Data) -> Data {
        var derived = [UInt8](repeating: 0, count: 32)
        _ = salt.withUnsafeBytes { saltBuf in
            secret.withCString { ptr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    ptr,
                    secret.utf8.count,
                    saltBuf.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    120_000,
                    &derived,
                    32
                )
            }
        }
        return Data(derived)
    }
}

struct BookshelfLockOverlayView: View {
    @ObservedObject var lock: BookshelfLockManager = .shared
    var onDismiss: () -> Void
    @State private var secretInput: String = ""

    var body: some View {
        ZStack {
            Color(red: 0.05, green: 0.05, blue: 0.06).ignoresSafeArea()
            VStack(spacing: 0) {
                HStack {
                    Button("닫기", action: onDismiss)
                        .font(.body.weight(.medium))
                        .foregroundStyle(.white.opacity(0.85))
                    Spacer()
                }
                .padding(.horizontal, 20)
                .padding(.top, 14)

                Spacer()

                VStack(spacing: 18) {
                    Image(systemName: "lock.rectangle.stack.fill")
                        .font(.system(size: 54))
                        .foregroundStyle(.blue)
                    Text("책장 잠금")
                        .font(.title2.weight(.bold))
                        .foregroundStyle(.white)
                    Text(lock.summaryDescription)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)

                    if lock.hasSecret {
                        SecureField(lock.lockKind == .pin ? "\(lock.pinLength)자리 PIN 입력" : "책장 비밀번호 입력", text: $secretInput)
                            .keyboardType(lock.lockKind == .pin ? .numberPad : .default)
                            .multilineTextAlignment(.center)
                            .font(.title3.monospacedDigit())
                            .padding(12)
                            .frame(width: 240)
                            .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 12))
                            .onSubmit {
                                if lock.verifySecret(secretInput) { secretInput = "" }
                            }
                    }

                    if !lock.errorMessage.isEmpty {
                        Text(lock.errorMessage)
                            .font(.caption)
                            .foregroundStyle(.red)
                            .multilineTextAlignment(.center)
                    }

                    HStack(spacing: 12) {
                        if lock.hasSecret {
                            Button {
                                if lock.verifySecret(secretInput) { secretInput = "" }
                            } label: {
                                Label("잠금 해제", systemImage: "key.fill")
                                    .padding(.horizontal, 16)
                                    .padding(.vertical, 10)
                            }
                            .buttonStyle(.borderedProminent)
                        }

                        if lock.useBiometrics || !lock.hasSecret {
                            Button {
                                Task { await lock.unlockWithDeviceAuth() }
                            } label: {
                                Label(lock.hasSecret ? "Face ID / Touch ID" : "Face ID / 암호로 열기", systemImage: "faceid")
                                    .padding(.horizontal, 16)
                                    .padding(.vertical, 10)
                            }
                            .buttonStyle(.borderedProminent)
                        }
                    }
                }
                .padding(28)

                Spacer()
            }
        }
        .task {
            await lock.unlockWithDeviceAuth()
        }
    }
}

struct BookshelfLockSettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var lock: BookshelfLockManager = .shared
    @State private var showSecretSetup = false

    private var enabledBinding: Binding<Bool> {
        Binding(
            get: { lock.isEnabled && lock.hasSecret },
            set: { next in
                if next {
                    showSecretSetup = true
                } else {
                    lock.disableLock()
                }
            }
        )
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("책장 잠금 사용", isOn: enabledBinding)
                    if lock.isEnabled && lock.hasSecret {
                        LabeledContent("적용 규칙") {
                            Text(lock.summaryDescription)
                                .font(.caption)
                                .foregroundStyle(.green)
                                .multilineTextAlignment(.trailing)
                        }
                        Toggle("Face ID / Touch ID 사용", isOn: $lock.useBiometrics)
                        Picker("자동 잠금 시간", selection: $lock.lockTimeout) {
                            ForEach(BookshelfLockTimeout.allCases) { option in
                                Text(option.title).tag(option)
                            }
                        }
                        Toggle("아이폰이 잠기면 같이 잠금", isOn: $lock.lockOnDeviceLock)
                        Toggle("앱 백그라운드 전환 시 즉시 잠금", isOn: $lock.lockOnBackground)
                        Button("PIN / 비밀번호 변경...") {
                            showSecretSetup = true
                        }
                    }
                } header: {
                    Text("책장 잠금")
                } footer: {
                    Text("책장 잠금 사용을 켜면 PIN(4~6자리) 또는 비밀번호 설정 창이 열립니다. ‘아이폰이 잠기면 같이 잠금’을 켜면 기기 화면이 잠길 때 책장도 함께 잠깁니다.")
                }

                if lock.isEnabled && lock.hasSecret {
                    Section {
                        Button {
                            lock.lockNow()
                            dismiss()
                        } label: {
                            Label("지금 책장 잠그기", systemImage: "lock.fill")
                        }
                    }
                }
            }
            .navigationTitle("책장 잠금 설정")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .sheet(isPresented: $showSecretSetup) {
                LockSecretSetupSheet(
                    title: lock.hasSecret ? "책장 잠금 암호 변경" : "책장 잠금 암호 설정",
                    initialKind: lock.lockKind
                ) { secret, kind in
                    lock.setSecret(secret, kind: kind) ? nil : lock.errorMessage
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - WireGuard .conf Profile Manager & DNS-over-HTTPS (Android VpnActivity + VpnProfiles Parity!)

// MARK: - WireGuard User-Space Tunnel Manager (Zero NetworkExtension Entitlement — 100% AltStore Compatible!)

@MainActor
final class WireGuardTunnelManager: ObservableObject {
    static let shared = WireGuardTunnelManager()

    @Published var connectedProfileID: UUID? = nil
    @Published var connectedProfileName: String? = nil
    @Published var connectedEndpoint: String? = nil
    @Published var activePort: UInt16 = 0
    @Published var isConnecting: Bool = false
    @Published var statusText: String = "연결 안 됨"
    @Published var rxBytes: UInt64 = 0
    @Published var txBytes: UInt64 = 0
    @Published var lastHandshakeSec: Int64 = 0
    @Published var lastError: String? = nil

    var onTunnelStateChanged: ((UInt16) -> Void)? = nil

    var isConnected: Bool { activePort > 0 && connectedProfileID != nil }

    private struct NativeStats: Decodable {
        let running: Bool
        let port: Int32
        let rxBytes: UInt64
        let txBytes: UInt64
        let lastHandshakeSec: Int64
    }

    func connect(profile: WireGuardProfile) async -> Bool {
        guard !isConnecting else { return false }
        isConnecting = true
        lastError = nil
        statusText = "연결 중…"

        let raw = profile.rawConfig
        let result: (port: Int32, errorMsg: String) = await Task.detached(priority: .userInitiated) {
            let port = raw.withCString { omyaru_wg_start($0) }
            if port > 0 {
                return (port, "")
            }
            var errMsg = "WireGuard 터널을 시작할 수 없습니다."
            if let cErr = omyaru_wg_last_error() {
                let s = String(cString: cErr)
                omyaru_wg_free_string(cErr)
                if !s.isEmpty { errMsg = s }
            }
            return (port, errMsg)
        }.value

        isConnecting = false
        if result.port > 0 && result.port <= 65535 {
            activePort = UInt16(result.port)
            connectedProfileID = profile.id
            connectedProfileName = profile.name
            connectedEndpoint = profile.endpoint
            statusText = "연결됨"
            rxBytes = 0
            txBytes = 0
            lastHandshakeSec = 0
            onTunnelStateChanged?(activePort)
            refreshStats()
            return true
        } else {
            activePort = 0
            connectedProfileID = nil
            connectedProfileName = nil
            connectedEndpoint = nil
            statusText = "연결 실패"
            lastError = result.errorMsg
            onTunnelStateChanged?(0)
            return false
        }
    }

    func disconnect() {
        omyaru_wg_stop()
        activePort = 0
        connectedProfileID = nil
        connectedProfileName = nil
        connectedEndpoint = nil
        rxBytes = 0
        txBytes = 0
        lastHandshakeSec = 0
        statusText = "연결 안 됨"
        onTunnelStateChanged?(0)
    }

    func refreshStats() {
        guard let cStr = omyaru_wg_stats_json() else { return }
        let jsonStr = String(cString: cStr)
        omyaru_wg_free_string(cStr)
        guard let data = jsonStr.data(using: .utf8),
              let stats = try? JSONDecoder().decode(NativeStats.self, from: data) else { return }
        if stats.running && stats.port > 0 {
            rxBytes = stats.rxBytes
            txBytes = stats.txBytes
            lastHandshakeSec = stats.lastHandshakeSec
        } else if activePort != 0 {
            activePort = 0
            connectedProfileID = nil
            connectedProfileName = nil
            connectedEndpoint = nil
            statusText = "연결 안 됨"
            onTunnelStateChanged?(0)
        }
    }

    @available(iOS 17.0, *)
    func activeProxyConfigurations(preferences: BrowserPreferences) -> [ProxyConfiguration] {
        if activePort > 0, let nwPort = NWEndpoint.Port(rawValue: activePort) {
            var cfg = ProxyConfiguration(socksv5Proxy: .hostPort(host: "127.0.0.1", port: nwPort))
            cfg.allowFailover = false
            return [cfg]
        }
        if preferences.proxyEnabled {
            let host = preferences.proxyHost.trimmingCharacters(in: .whitespacesAndNewlines)
            if !host.isEmpty && preferences.proxyPort > 0 && preferences.proxyPort <= 65535,
               let nwPort = NWEndpoint.Port(rawValue: UInt16(preferences.proxyPort)) {
                let ep = NWEndpoint.hostPort(host: NWEndpoint.Host(host), port: nwPort)
                var cfg = preferences.proxyIsSOCKS5
                    ? ProxyConfiguration(socksv5Proxy: ep)
                    : ProxyConfiguration(httpCONNECTProxy: ep)
                cfg.allowFailover = false
                return [cfg]
            }
        }
        return []
    }

    static func isSystemVpnActive() -> Bool {
        guard let cfDict = CFNetworkCopySystemProxySettings()?.takeRetainedValue() as? [String: Any],
              let scoped = cfDict["__SCOPED__"] as? [String: Any] else {
            return false
        }
        for key in scoped.keys {
            let lower = key.lowercased()
            if lower.contains("tap") || lower.contains("tun") || lower.contains("ppp") || lower.contains("ipsec") || lower.contains("utun") {
                return true
            }
        }
        return false
    }

    static func formatBytes(_ bytes: UInt64) -> String {
        let kb = Double(bytes) / 1024.0
        if kb < 1024.0 { return String(format: "%.1f KB", kb) }
        let mb = kb / 1024.0
        if mb < 1024.0 { return String(format: "%.2f MB", mb) }
        return String(format: "%.2f GB", mb / 1024.0)
    }
}

struct WireGuardProfile: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var endpoint: String
    var address: String
    var dns: String
    var rawConfig: String
}

enum DnsProviderPreset: String, CaseIterable, Codable, Identifiable {
    case system = "system"
    case cloudflare = "1.1.1.1"
    case google = "8.8.8.8"
    case quad9 = "9.9.9.9"
    case adguard = "adguard"
    case custom = "custom"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "시스템 기본 DNS (우회 안 함)"
        case .cloudflare: return "Cloudflare DNS (1.1.1.1 DoH)"
        case .google: return "Google DNS (8.8.8.8 DoH)"
        case .quad9: return "Quad9 보안 DNS (9.9.9.9 DoH)"
        case .adguard: return "AdGuard DNS (광고·추적기 차단)"
        case .custom: return "사용자 지정 DoH 서버"
        }
    }

    var dohURL: String {
        switch self {
        case .system: return ""
        case .cloudflare: return "https://cloudflare-dns.com/dns-query"
        case .google: return "https://dns.google/dns-query"
        case .quad9: return "https://dns.quad9.net/dns-query"
        case .adguard: return "https://dns.adguard-dns.com/dns-query"
        case .custom: return ""
        }
    }

    var bootstrapIPs: [String] {
        switch self {
        case .system: return []
        case .cloudflare: return ["1.1.1.1", "1.0.0.1"]
        case .google: return ["8.8.8.8", "8.8.4.4"]
        case .quad9: return ["9.9.9.9", "149.112.112.112"]
        case .adguard: return ["94.140.14.14", "94.140.15.15"]
        case .custom: return ["1.1.1.1"]
        }
    }
}

struct MobileConfigDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.xml, .data] }
    var data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

struct NetworkPrivacySheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    @ObservedObject private var wg = WireGuardTunnelManager.shared
    @State private var exportingProfile = false
    @State private var profileDoc = MobileConfigDocument(data: Data())
    @State private var importingWireGuard = false
    @State private var sharingConfURL: URL? = nil
    @State private var checkingConnection = false
    @State private var connectionResult = ""
    @State private var alertMessage: String? = nil
    @State private var systemVpnActive = WireGuardTunnelManager.isSystemVpnActive()
    @State private var statsPollTask: Task<Void, Never>? = nil

    var body: some View {
        NavigationStack {
            Form {
                // 1. 보안 DNS (DNS 우회) — Android VpnActivity 섹션 1
                Section {
                    Picker("보안 DNS (DoH)", selection: $model.preferences.dnsPreset) {
                        ForEach(DnsProviderPreset.allCases) { preset in
                            Text(preset.title).tag(preset)
                        }
                    }
                    .onChange(of: model.preferences.dnsPreset) { preset in
                        if preset != .custom {
                            model.preferences.customDoHURL = preset.dohURL
                        }
                        model.savePreferences()
                    }

                    if model.preferences.dnsPreset == .custom {
                        TextField("https://.../dns-query", text: $model.preferences.customDoHURL)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .keyboardType(.URL)
                            .onChange(of: model.preferences.customDoHURL) { _ in model.savePreferences() }
                    } else if !model.preferences.dnsPreset.dohURL.isEmpty {
                        LabeledContent("DoH 엔드포인트") {
                            Text(model.preferences.dnsPreset.dohURL).font(.caption).foregroundStyle(.secondary)
                        }
                        LabeledContent("부트스트랩 IP") {
                            Text(model.preferences.dnsPreset.bootstrapIPs.joined(separator: ", "))
                                .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                    }

                    if model.preferences.dnsPreset != .system {
                        Button {
                            let xml = generateDoHMobileConfig()
                            profileDoc = MobileConfigDocument(data: Data(xml.utf8))
                            exportingProfile = true
                        } label: {
                            Label("iOS 시스템 암호화 DNS 프로필(.mobileconfig) 내보내기", systemImage: "network.badge.shield.half.filled")
                        }
                    }
                } header: {
                    Text("보안 DNS (DNS 우회)")
                } footer: {
                    Text("기기 IP는 바꾸지 않고 도메인 질의(DNS)만 암호화된 DoH로 우회하여 ISP 차단 및 DNS 감청을 방지합니다.")
                }

                // 2. 내장 WireGuard VPN — Android VpnActivity 섹션 2 (실제 연결/해제 지원!)
                Section {
                    // 상태 배너
                    HStack(spacing: 10) {
                        Circle()
                            .fill(wg.isConnected ? Color.green : (systemVpnActive ? Color.blue : Color.gray.opacity(0.6)))
                            .frame(width: 10, height: 10)
                        VStack(alignment: .leading, spacing: 2) {
                            if wg.isConnected, let name = wg.connectedProfileName {
                                Text("연결됨 · \(name)")
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(.green)
                                Text("수신 \(WireGuardTunnelManager.formatBytes(wg.rxBytes)) · 송신 \(WireGuardTunnelManager.formatBytes(wg.txBytes))")
                                    .font(.caption2.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            } else if wg.isConnecting {
                                Text("WireGuard 연결 중…")
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(.orange)
                            } else if systemVpnActive {
                                Text("iOS 시스템 VPN 활성 중")
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(.blue)
                            } else {
                                Text("연결 안 됨")
                                    .font(.subheadline.weight(.medium))
                                    .foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        if wg.isConnecting {
                            ProgressView()
                        } else if wg.isConnected {
                            Button("연결 해제", role: .destructive) {
                                wg.disconnect()
                                model.status = "WireGuard VPN 연결을 해제했습니다."
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                    }

                    Button {
                        importingWireGuard = true
                    } label: {
                        Label("WireGuard 설정 추가 (.conf)", systemImage: "plus.circle.fill")
                    }

                    if model.wireGuardProfiles.isEmpty {
                        Text("저장된 WireGuard 프로필이 없습니다.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(model.wireGuardProfiles) { prof in
                            let isThisConnected = (wg.connectedProfileID == prof.id && wg.isConnected)
                            VStack(alignment: .leading, spacing: 6) {
                                HStack(spacing: 8) {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(prof.name)
                                            .font(.subheadline.weight(.semibold))
                                        Text("Endpoint: \(prof.endpoint)")
                                            .font(.caption2)
                                            .foregroundStyle(.secondary)
                                            .lineLimit(1)
                                        Text("Address: \(prof.address) · DNS: \(prof.dns)")
                                            .font(.caption2)
                                            .foregroundStyle(.secondary)
                                            .lineLimit(1)
                                    }
                                    Spacer(minLength: 6)
                                    if isThisConnected {
                                        Button("해제", role: .destructive) {
                                            wg.disconnect()
                                            model.status = "WireGuard VPN 연결을 해제했습니다."
                                        }
                                        .buttonStyle(.borderedProminent)
                                        .tint(.red)
                                        .controlSize(.small)
                                    } else {
                                        Button("연결") {
                                            Task {
                                                let ok = await wg.connect(profile: prof)
                                                if ok {
                                                    model.status = "WireGuard VPN 연결됨 (\(prof.name))"
                                                } else if let err = wg.lastError {
                                                    alertMessage = "WireGuard 연결 실패: \(err)"
                                                }
                                            }
                                        }
                                        .buttonStyle(.borderedProminent)
                                        .controlSize(.small)
                                        .disabled(wg.isConnecting)
                                    }
                                }

                                HStack(spacing: 12) {
                                    Button {
                                        if let fileURL = writeTempConfFile(for: prof) {
                                            sharingConfURL = fileURL
                                        }
                                    } label: {
                                        Label("WireGuard 앱으로 .conf 공유", systemImage: "square.and.arrow.up")
                                            .font(.caption)
                                    }
                                    .buttonStyle(.plain)
                                    .foregroundStyle(.blue)
                                }
                            }
                            .padding(.vertical, 2)
                            .swipeActions {
                                Button("삭제", role: .destructive) {
                                    if wg.connectedProfileID == prof.id {
                                        wg.disconnect()
                                    }
                                    model.wireGuardProfiles.removeAll { $0.id == prof.id }
                                    model.persist()
                                }
                            }
                        }
                    }

                    Button {
                        Task { await checkInternetConnectivity() }
                    } label: {
                        HStack {
                            Label("인터넷 연결 검사 (외부 IP · 지연 시간)", systemImage: "arrow.clockwise.circle")
                            Spacer()
                            if checkingConnection { ProgressView() }
                        }
                    }
                    .disabled(checkingConnection)

                    if !connectionResult.isEmpty {
                        Text(connectionResult)
                            .font(.caption.monospaced())
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text("내장 VPN (WireGuard .conf) 및 연결 검사")
                } footer: {
                    if #available(iOS 17.0, *) {
                        Text("Omyaru 내장 WireGuard 엔진으로 .conf 프로필에 바로 연결해 브라우저의 모든 웹·DNS 트래픽을 암호화 터널로 우회합니다. 기기 전체 시스템 VPN이 필요하면 '.conf 공유'로 공식 WireGuard 앱에 추가할 수 있습니다.")
                    } else {
                        Text("iOS 17 이상에서 Omyaru 내장 WireGuard 연결을 지원합니다. iOS 16에서는 '.conf 공유'로 공식 WireGuard 앱에 추가해 연결할 수 있습니다.")
                    }
                }

                // 3. SOCKS5 / HTTP 프록시
                Section("프록시 설정 (SOCKS5 / HTTP)") {
                    Toggle("프록시 프로필 활성화", isOn: $model.preferences.proxyEnabled)
                        .onChange(of: model.preferences.proxyEnabled) { _ in
                            model.savePreferences()
                            Task { await model.applyNetworkProxyConfiguration() }
                        }
                    if model.preferences.proxyEnabled {
                        Picker("유형", selection: $model.preferences.proxyIsSOCKS5) {
                            Text("SOCKS5").tag(true)
                            Text("HTTP CONNECT").tag(false)
                        }
                        .onChange(of: model.preferences.proxyIsSOCKS5) { _ in
                            model.savePreferences()
                            Task { await model.applyNetworkProxyConfiguration() }
                        }
                        TextField("호스트 (예: 127.0.0.1)", text: $model.preferences.proxyHost)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .onSubmit {
                                model.savePreferences()
                                Task { await model.applyNetworkProxyConfiguration() }
                            }
                        TextField("포트 (예: 1080)", value: $model.preferences.proxyPort, format: .number)
                            .keyboardType(.numberPad)
                            .onSubmit {
                                model.savePreferences()
                                Task { await model.applyNetworkProxyConfiguration() }
                            }
                        Button("프록시 설정 즉시 적용") {
                            model.savePreferences()
                            Task { await model.applyNetworkProxyConfiguration(forceRebuild: true) }
                        }
                    }
                }
            }
            .navigationTitle("VPN · 보안 DNS")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .onAppear {
                systemVpnActive = WireGuardTunnelManager.isSystemVpnActive()
                wg.refreshStats()
                statsPollTask?.cancel()
                statsPollTask = Task { @MainActor in
                    while !Task.isCancelled {
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        guard !Task.isCancelled else { break }
                        systemVpnActive = WireGuardTunnelManager.isSystemVpnActive()
                        if wg.isConnected {
                            wg.refreshStats()
                        }
                    }
                }
            }
            .onDisappear {
                statsPollTask?.cancel()
                statsPollTask = nil
            }
            .fileImporter(isPresented: $importingWireGuard, allowedContentTypes: [.data, .plainText]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    if let text = try? String(contentsOf: url, encoding: .utf8),
                       let parsed = Self.parseWireGuardConfig(name: url.deletingPathExtension().lastPathComponent, text: text) {
                        model.wireGuardProfiles.append(parsed)
                        model.persist()
                    } else {
                        alertMessage = "올바른 WireGuard .conf 파일인지 확인해 주세요. ([Interface] PrivateKey 및 [Peer] PublicKey/Endpoint 필요)"
                    }
                }
            }
            .sheet(isPresented: Binding(get: { sharingConfURL != nil }, set: { if !$0 { sharingConfURL = nil } })) {
                if let url = sharingConfURL {
                    ActivityShareSheet(items: [url])
                }
            }
            .fileExporter(
                isPresented: $exportingProfile,
                document: profileDoc,
                contentType: .xml,
                defaultFilename: "Omyaru-Encrypted-DNS.mobileconfig"
            ) { _ in }
            .alert("VPN 설정", isPresented: Binding(get: { alertMessage != nil }, set: { if !$0 { alertMessage = nil } })) {
                Button("확인") { alertMessage = nil }
            } message: {
                Text(alertMessage ?? "")
            }
        }
        .preferredColorScheme(.dark)
    }

    private func writeTempConfFile(for profile: WireGuardProfile) -> URL? {
        let safe = profile.name
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: ":", with: "_")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let filename = (safe.isEmpty ? "wireguard" : safe) + ".conf"
        let dest = FileManager.default.temporaryDirectory.appendingPathComponent(filename)
        do {
            try profile.rawConfig.write(to: dest, atomically: true, encoding: .utf8)
            return dest
        } catch {
            alertMessage = ".conf 파일 준비 실패: \(error.localizedDescription)"
            return nil
        }
    }

    private func checkInternetConnectivity() async {
        checkingConnection = true
        defer { checkingConnection = false }
        let start = Date()
        do {
            let cfg = URLSessionConfiguration.ephemeral
            cfg.timeoutIntervalForRequest = 12
            if #available(iOS 17.0, *) {
                let proxies = wg.activeProxyConfigurations(preferences: model.preferences)
                if !proxies.isEmpty {
                    cfg.proxyConfigurations = proxies
                }
            }
            let session = URLSession(configuration: cfg)
            defer { session.invalidateAndCancel() }
            let (data, _) = try await session.data(from: URL(string: "https://1.1.1.1/cdn-cgi/trace")!)
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            let body = String(data: data, encoding: .utf8) ?? ""
            var ip = "-"
            var loc = "-"
            var tls = "-"
            for line in body.components(separatedBy: .newlines) {
                if line.hasPrefix("ip=") { ip = String(line.dropFirst(3)) }
                else if line.hasPrefix("loc=") { loc = String(line.dropFirst(4)) }
                else if line.hasPrefix("tls=") { tls = String(line.dropFirst(4)) }
            }
            let via = wg.isConnected ? "WireGuard 경유" : (model.preferences.proxyEnabled ? "프록시 경유" : "직접 연결")
            connectionResult = "[\(via)] 정상 (\(ms)ms) · IP: \(ip) · 국가: \(loc) · \(tls)"
            if wg.isConnected { wg.refreshStats() }
        } catch {
            connectionResult = "연결 검사 실패: \(error.localizedDescription)"
        }
    }

    private static func parseWireGuardConfig(name: String, text: String) -> WireGuardProfile? {
        guard text.count <= 64 * 1024 else { return nil }
        var hasPrivate = false
        var hasPublic = false
        var endpoint = ""
        var address = ""
        var dns = ""
        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix("[") { continue }
            let parts = line.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 2 else { continue }
            switch parts[0].lowercased() {
            case "privatekey": hasPrivate = !parts[1].isEmpty
            case "publickey": hasPublic = !parts[1].isEmpty
            case "endpoint": endpoint = parts[1]
            case "address": address = parts[1]
            case "dns": dns = parts[1]
            case "preup", "postup", "predown", "postdown":
                return nil // Reject external shell scripts just like Android VpnProfiles!
            default: break
            }
        }
        guard hasPrivate, hasPublic, !endpoint.isEmpty else { return nil }
        return WireGuardProfile(
            name: name.isEmpty ? "WireGuard" : name,
            endpoint: endpoint,
            address: address.isEmpty ? "-" : address,
            dns: dns.isEmpty ? "-" : dns,
            rawConfig: text
        )
    }

    private func generateDoHMobileConfig() -> String {
        let preset = model.preferences.dnsPreset
        let serverURL = preset == .custom ? model.preferences.customDoHURL : preset.dohURL
        let ips = preset.bootstrapIPs.map { "<string>\($0)</string>" }.joined(separator: "\n              ")
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>PayloadContent</key>
          <array>
            <dict>
              <key>DNSSettings</key>
              <dict>
                <key>DNSProtocol</key>
                <string>HTTPS</string>
                <key>ServerAddresses</key>
                <array>
                  \(ips)
                </array>
                <key>ServerURL</key>
                <string>\(serverURL)</string>
              </dict>
              <key>PayloadDescription</key>
              <string>Omyaru Browser Encrypted DNS-over-HTTPS</string>
              <key>PayloadDisplayName</key>
              <string>Omyaru DoH (\(preset.title))</string>
              <key>PayloadIdentifier</key>
              <string>cuttan.omyaru.app.dns.payload</string>
              <key>PayloadType</key>
              <string>com.apple.dnsSettings.managed</string>
              <key>PayloadUUID</key>
              <string>9B3E31B2-5E10-4D32-9C7A-111122223333</string>
              <key>PayloadVersion</key>
              <integer>1</integer>
            </dict>
          </array>
          <key>PayloadDisplayName</key>
          <string>Omyaru Encrypted DNS (\(preset.title))</string>
          <key>PayloadIdentifier</key>
          <string>cuttan.omyaru.app.dns</string>
          <key>PayloadRemovalDisallowed</key>
          <false/>
          <key>PayloadType</key>
          <string>Configuration</string>
          <key>PayloadUUID</key>
          <string>9B3E31B2-5E10-4D32-9C7A-444455556666</string>
          <key>PayloadVersion</key>
          <integer>1</integer>
        </dict>
        </plist>
        """
    }
}

// MARK: - Built-in Extensions & UserScript (.user.js) Manager

struct UserScriptExtension: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var matchPattern: String = "*://*/*"
    var runAtDocumentStart: Bool = false
    var isEnabled: Bool = true
    var code: String
}

struct ExtensionSettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    @State private var showAddScript = false
    @State private var showImportScript = false
    @State private var scriptName = ""
    @State private var scriptMatch = "*://*/*"
    @State private var scriptStart = false
    @State private var scriptCode = ""
    @State private var installURL = ""
    @State private var statusText = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Omyaru 내장 확장 기능 (WebExtensions)") {
                    Toggle(isOn: $model.preferences.enableImagePreloader) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("웹 이미지 고속 선로딩 (Image Preloader)")
                            Text("스크롤 전 뷰포트 하단 2200px 이내 지연 이미지를 미리 로드합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.hideCookieNotices) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("쿠키 동의 팝업 자동 숨김")
                            Text("웹사이트 하단/중앙 쿠키 배너를 자동으로 숨깁니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.unlockCopyAndContextMenu) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("우클릭 · 드래그 · 복사 방지 해제")
                            Text("텍스트 선택 및 길게 누르기 제한을 해제합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.enableMediaPiPHelper) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("동영상 PiP · 백그라운드 재생 보조")
                            Text("HTML5 비디오에서 Picture-in-Picture를 활성화합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.forceWebDarkMode) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("웹 페이지 다크 모드 강제 적용")
                            Text("밝은 배경 웹사이트를 어두운 테마로 변환합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
                .onChange(of: model.preferences) { _ in
                    model.savePreferences()
                    model.applyUserScriptsToAllWebViews()
                }

                Section("유저스크립트 (.user.js) URL 또는 파일 설치") {
                    HStack {
                        TextField("https://.../*.user.js 주소 입력", text: $installURL)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .keyboardType(.URL)
                        Button("URL 설치") {
                            Task { await installScriptFromURL() }
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(installURL.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                    HStack(spacing: 12) {
                        Button {
                            showImportScript = true
                        } label: {
                            Label(".user.js / .js 파일 불러오기", systemImage: "doc.badge.plus")
                        }
                        Spacer()
                        Button {
                            scriptName = ""
                            scriptMatch = "*://*/*"
                            scriptStart = false
                            scriptCode = ""
                            showAddScript = true
                        } label: {
                            Label("직접 작성", systemImage: "plus.circle.fill")
                        }
                    }
                    if !statusText.isEmpty {
                        Text(statusText).font(.caption).foregroundStyle(.secondary)
                    }
                }

                Section("설치된 유저스크립트 (\(model.userScripts.count))") {
                    if model.userScripts.isEmpty {
                        Text("추가된 커스텀 유저스크립트가 없습니다.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach($model.userScripts) { $script in
                            HStack {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(script.name).font(.subheadline.weight(.semibold))
                                    Text("\(script.matchPattern) · \(script.runAtDocumentStart ? "document-start" : "document-end")")
                                        .font(.caption2)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                Toggle("", isOn: $script.isEnabled)
                                    .labelsHidden()
                                    .onChange(of: script.isEnabled) { _ in
                                        model.persist()
                                        model.applyUserScriptsToAllWebViews()
                                    }
                            }
                            .swipeActions {
                                Button("삭제", role: .destructive) {
                                    model.userScripts.removeAll { $0.id == script.id }
                                    model.persist()
                                    model.applyUserScriptsToAllWebViews()
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("확장 기능 · 유저스크립트")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .fileImporter(isPresented: $showImportScript, allowedContentTypes: [.data, .plainText, .sourceCode]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    if let code = try? String(contentsOf: url, encoding: .utf8) {
                        addParsedUserScript(code: code, fallbackName: url.deletingPathExtension().lastPathComponent)
                    }
                }
            }
            .sheet(isPresented: $showAddScript) {
                NavigationStack {
                    Form {
                        TextField("스크립트 이름", text: $scriptName)
                        TextField("대상 도메인 (예: *://*/* 또는 toptoon.com)", text: $scriptMatch)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                        Toggle("문서 시작 시점(document-start) 실행", isOn: $scriptStart)
                        Section("JavaScript 코드") {
                            TextEditor(text: $scriptCode)
                                .font(.system(.caption, design: .monospaced))
                                .frame(minHeight: 220)
                        }
                    }
                    .navigationTitle("새 유저스크립트")
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button("취소") { showAddScript = false }
                        }
                        ToolbarItem(placement: .confirmationAction) {
                            Button("추가") {
                                let name = scriptName.trimmingCharacters(in: .whitespacesAndNewlines)
                                guard !name.isEmpty, !scriptCode.isEmpty else { return }
                                model.userScripts.append(UserScriptExtension(
                                    name: name,
                                    matchPattern: scriptMatch.isEmpty ? "*://*/*" : scriptMatch,
                                    runAtDocumentStart: scriptStart,
                                    isEnabled: true,
                                    code: scriptCode
                                ))
                                model.persist()
                                model.applyUserScriptsToAllWebViews()
                                showAddScript = false
                            }
                        }
                    }
                }
                .preferredColorScheme(.dark)
            }
        }
        .preferredColorScheme(.dark)
    }

    private func installScriptFromURL() async {
        guard let url = URL(string: installURL.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            statusText = "올바른 URL이 아닙니다."
            return
        }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let code = String(data: data, encoding: .utf8), !code.isEmpty else {
                statusText = "스크립트 내용을 읽지 못했습니다."
                return
            }
            addParsedUserScript(code: code, fallbackName: url.deletingPathExtension().lastPathComponent)
            installURL = ""
        } catch {
            statusText = "설치 실패: \(error.localizedDescription)"
        }
    }

    private func addParsedUserScript(code: String, fallbackName: String) {
        var name = fallbackName.isEmpty ? "UserScript" : fallbackName
        var match = "*://*/*"
        var start = false
        for line in code.components(separatedBy: .newlines).prefix(60) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("// @name"), let val = trimmed.dropFirst(8).trimmingCharacters(in: .whitespaces) as String?, !val.isEmpty {
                name = val
            } else if trimmed.hasPrefix("// @match"), let val = trimmed.dropFirst(9).trimmingCharacters(in: .whitespaces) as String?, !val.isEmpty {
                match = val
            } else if trimmed.hasPrefix("// @run-at") && trimmed.contains("document-start") {
                start = true
            }
        }
        model.userScripts.append(UserScriptExtension(
            name: name,
            matchPattern: match,
            runAtDocumentStart: start,
            isEnabled: true,
            code: code
        ))
        model.persist()
        model.applyUserScriptsToAllWebViews()
        statusText = "‘\(name)’ 유저스크립트가 설치되었습니다."
    }
}
