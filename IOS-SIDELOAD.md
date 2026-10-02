# iPhone sideload guide (Where U At)

**Important:** A signed `.ipa` cannot be built on Linux. This repo’s CI/dev boxes are Linux; packaging for iPhone requires **macOS + Xcode** (and either a free Apple ID or a paid Apple Developer Program membership).

Bundle id: **`com.familytracker.whereuat`**  
Firebase iOS app: already in `Platforms/iOS/GoogleService-Info.plist` (project `whereuat-firebase`, `GOOGLE_APP_ID` `1:438926374560:ios:c8a64824e5340e5263bbc5`).

---

## 1. What is already ready in this repo

| Item | Status |
|------|--------|
| TFM `net10.0-ios` | Enabled on macOS/Windows (skipped on Linux in `.csproj`) |
| `ApplicationId` | `com.familytracker.whereuat` |
| `Platforms/iOS/Info.plist` | Location when-in-use string + Firebase URL scheme |
| `Platforms/iOS/Entitlements.plist` | Minimal (no push / App Groups) |
| `Platforms/iOS/GoogleService-Info.plist` | Present; bundled via `BundleResource` |
| Firebase init | `MauiProgram` → `CrossFirebase.Initialize()` on iOS `WillFinishLaunching` |
| Privacy Manifest | `PrivacyInfo.xcprivacy` (incl. UserDefaults / Preferences) |
| Min iOS | 15.0 |

## 2. Gaps you close on the iMac (not on Linux)

1. **Install tools**
   - macOS with recent **Xcode** (App Store) + open once to accept license / install components.
   - [.NET 10 SDK](https://dotnet.microsoft.com/download) + MAUI workload:
     ```bash
     dotnet workload install maui
     ```
   - Optional: **Visual Studio for Mac** is retired; use **VS Code** + C# Dev Kit, **JetBrains Rider**, or CLI + Xcode.

2. **Apple ID — free vs paid ($99/year)**

   | | Free Apple ID | Paid Apple Developer Program (~US$99/yr) |
   |--|---------------|------------------------------------------|
   | Install on your own iPhone | Yes (via Xcode) | Yes |
   | Certificate lifetime | **~7 days** then re-sign / reinstall | **1 year** |
   | Devices | Limited; personal team | Up to 100 devices (dev) |
   | TestFlight / App Store | No | Yes |
   | Push / many entitlements | Mostly no | Yes |

3. **Firebase Console**
   - Confirm an **iOS app** exists for bundle id `com.familytracker.whereuat` in project **whereuat-firebase**.
   - If you ever regenerate the plist, replace `Platforms/iOS/GoogleService-Info.plist` and keep `BUNDLE_ID` = `com.familytracker.whereuat`.
   - Email/password Auth + Realtime Database rules are the same as Android (see README).

4. **Signing identity on the Mac**
   - Xcode → Settings → Accounts → add your Apple ID → Manage Certificates → **Apple Development**.
   - First build will create a free/personal team provisioning profile for `com.familytracker.whereuat` (or ask you to register the App ID).

---

## 3. Sideload options (pick one)

### A. Free Apple ID — 7-day install via Xcode / `dotnet` (recommended first try)

- Plug iPhone into iMac with a cable.
- Trust the computer on the phone; on Mac: Finder → iPhone → Trust if prompted.
- On iPhone: **Settings → Privacy & Security → Developer Mode** → On (iOS 16+), then reboot if asked.
- On iPhone after install: **Settings → General → VPN & Device Management** → trust your developer certificate.

**Pros:** No $99; official path. **Cons:** App expires ~every 7 days; must rebuild/reinstall.

### B. AltStore / Sideloadly (still free Apple ID)

- Build an `.ipa` on the Mac first (`dotnet publish` — see below), then install with [Sideloadly](https://sideloadly.io/) or [AltStore](https://altstore.io/).
- Same **7-day** free-account limit (AltStore can refresh on Wi‑Fi if set up carefully).
- Use only with **your own** Apple ID and device.

### C. TestFlight (paid Apple Developer Program)

1. Enroll at [developer.apple.com](https://developer.apple.com/programs/).
2. Create App Store Connect app with bundle id `com.familytracker.whereuat`.
3. Archive + upload from Xcode (or `dotnet publish` + Transporter).
4. Invite testers via TestFlight — builds last up to 90 days; no cable required after first install.

---

## 4. Step-by-step: plug iPhone into iMac and run

### 4.1 One-time device setup

1. Unlock iPhone → connect USB → **Trust This Computer**.
2. Enable **Developer Mode** (iOS 16+): Settings → Privacy & Security → Developer Mode → On → reboot.
3. Note the device name; keep the phone unlocked during the first deploy.

### 4.2 Clone / open the project

```bash
git clone https://github.com/AuroraAustralisDownunder/WhereUAtNative.git
cd WhereUAtNative
dotnet restore
```

### 4.3 Option — CLI deploy (Debug, free team)

List devices (after Xcode tools installed):

```bash
xcrun xctrace list devices
# or: dotnet build -t:Run -f net10.0-ios -p:_DeviceName="<iPhone name>"
```

Run on the connected iPhone:

```bash
dotnet build -t:Run -f net10.0-ios -c Debug
```

If multiple devices, add:

```bash
dotnet build -t:Run -f net10.0-ios -c Debug -p:_DeviceName="Meta’s iPhone"
```

Signing tips if the build fails:

```bash
# Use automatic signing with your Team ID (10-char id from developer.apple.com or Xcode Accounts)
dotnet build -t:Run -f net10.0-ios -c Debug \
  -p:CodesignKey="Apple Development: Your Name (XXXXXXXXXX)" \
  -p:CodesignProvision="Automatic"
```

Or open the generated Xcode workspace under `bin/.../ios/...` / use Rider’s iOS run config and set **Team** to your Personal Team.

### 4.4 Option — Xcode UI

1. `dotnet build -f net10.0-ios` once so native assets generate.
2. Open the `.xcodeproj` / `.xcworkspace` produced under the build output (path printed in build log), **or** use Rider → Run → iOS Device.
3. Signing & Capabilities → **Team** = your Apple ID personal team → Bundle Identifier = `com.familytracker.whereuat`.
4. Select your physical iPhone as the run destination → Run (▶).

### 4.5 After install

1. If iOS blocks launch: **Settings → General → VPN & Device Management** → trust the developer.
2. Sign in with a Firebase email/password user (same as Android).
3. Grant **location while using the app** only when you turn on Share my location.

### 4.6 Publish an `.ipa` on the Mac (for Sideloadly / TestFlight)

```bash
dotnet publish -f net10.0-ios -c Release \
  -p:BuildIpa=true \
  -p:CodesignKey="Apple Development: Your Name (XXXXXXXXXX)"
```

IPA typically lands under something like:

`bin/Release/net10.0-ios/ios-arm64/publish/*.ipa`

- **Free account:** development-signed IPA for Sideloadly/AltStore (7-day).
- **Paid account:** use Distribution / App Store signing for TestFlight upload.

---

## 5. What this Linux environment will never produce

- No signed `.ipa` artifact from this box.
- No Apple code-signing certificates or provisioning profiles.
- Android APKs in the repo are unrelated to iOS install.

When someone asks for an “iPhone package” from Linux, the honest answer is: **prep the project here; build and sideload on the iMac** using this doc.

---

## 6. Quick checklist

- [ ] Xcode installed + Apple ID added
- [ ] `dotnet workload install maui`
- [ ] iPhone trusted + Developer Mode on
- [ ] Bundle id stays `com.familytracker.whereuat`
- [ ] `GoogleService-Info.plist` present
- [ ] First launch: trust developer certificate on device
- [ ] Free ID: expect reinstall ~every 7 days (or upgrade to paid + TestFlight)
