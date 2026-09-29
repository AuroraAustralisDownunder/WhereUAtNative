# Where U At (WhereUAtNative)

Family Tracker — a privacy-first .NET MAUI app for Android and iOS. Firebase email/password authentication plus **opt-in** location sharing and a lightweight map shell. Location is **off by default** and never uploaded in this release.

## Open the project

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download) with the MAUI workload.
2. Open `WhereUAtNative.csproj` in **Visual Studio 2022/2026** or **JetBrains Rider**.
3. Select an Android or iOS target and run.

```bash
dotnet restore
dotnet build -t:Run -f net10.0-android
```

## App identity

| Setting | Value |
|--------|--------|
| Application title | Where U At |
| Android / iOS package id | `com.familytracker.whereuat` |
| Firebase project | `whereuat-firebase` |

Firebase config files already live under:

- `Platforms/Android/google-services.json`
- `Platforms/iOS/GoogleService-Info.plist`

## Testing login

Create (or use) a **Firebase Authentication** email/password user in the `whereuat-firebase` console. The app has login only — no in-app sign-up UI yet.

1. Launch on a device or emulator.
2. Sign in with the Firebase email/password user.
3. You should land on Home with welcome text and Sign out.

## Location sharing (opt-in)

Location is **local-only** in this build:

- Sharing defaults to **Off** (device preference `location_sharing_enabled` = false).
- The Home switch **Share my location** requests **when-in-use** permission, then reads GPS.
- Coords shown on Home/Map are rounded (~4 decimal places) for a privacy-friendly display.
- Nothing is written to Firestore or any server yet.
- Sign-out turns sharing off again for a clean next session.
- While sharing is on and Home is visible, position refreshes about every 30 seconds (cancelled when you leave Home or turn sharing off).

### Permissions

| Platform | What we declare |
|----------|-----------------|
| Android | `ACCESS_COARSE_LOCATION`, `ACCESS_FINE_LOCATION` (foreground / when-in-use only — no background location yet) |
| iOS / Mac Catalyst | `NSLocationWhenInUseUsageDescription` only (no Always) |

### How to test the toggle

1. Sign in on a physical device or an emulator with mock / GPS location enabled.
2. On Home, leave **Share my location** Off — status should read **Off**.
3. Turn the switch **On** — accept the system permission prompt.
4. Status should move to **Waiting for GPS…** then **On — lat, lon** (rounded).
5. Deny permission (or revoke in Settings) — sharing stays/returns **Off** with a friendly **Permission needed** message; the app must not crash.
6. Use the toolbar **Map** item — with sharing on and a fix, you should see a **You** pin; with sharing off, an empty-state message instead.
7. Turn sharing **Off** — updates stop and status returns to **Off**.

## Maps (OpenStreetMap)

The map UI uses a **WebView** with **Leaflet** and free **OpenStreetMap** tiles (`Resources/Raw/map.html`). No Google Maps API key is required on any platform.

- Pin is shown only when location sharing is on and a GPS fix exists.
- Otherwise the map page shows an empty-state message (same privacy rules as before).
- Leaflet JS/CSS load from the unpkg CDN; OSM tiles need network access (`INTERNET` permission already declared).

## Privacy defaults (this release)

- Credentials are never logged.
- Login failures show a generic message.
- Precise location is never logged.
- Location is not requested until the user turns sharing on.
- No background / Always location.
- No location upload to other users or Firestore yet.

## Note about repo docs

`Application for access to health information.docx` is unrelated documentation left in the repository; it is not part of the app.

## Next up

- Sharing locations with family (Firestore) — still opt-in
- Background tracking only if product explicitly requires it
