# Where U At (WhereUAtNative)

Family Tracker — a privacy-first .NET MAUI app for Android and iOS. Firebase email/password authentication, **opt-in** location sharing, family groups, and a lightweight OpenStreetMap map. Location is **off by default** and only uploaded while Share my location is ON **and** you belong to a family.

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
| Display version | `0.2.2` (versionCode `5`) |
| Firebase project | `whereuat-firebase` |

Firebase config files already live under:

- `Platforms/Android/google-services.json`
- `Platforms/iOS/GoogleService-Info.plist`

## Testing login

Create (or use) a **Firebase Authentication** email/password user in the `whereuat-firebase` console. The app has login only — no in-app sign-up UI yet.

1. Launch on a device or emulator.
2. Sign in with the Firebase email/password user.
3. You should land straight on the **Map** (full-bleed OSM). Use the mid-right green pin FAB to share location, **Out** (below it) to sign out, and **Settings** for family/account.

## Location sharing (opt-in)

- Sharing defaults to **Off** (device preference `location_sharing_enabled` = false).
- The map **pin FAB** (mid-right, vertically centered / slightly high so Leaflet’s Unlock stays clear) toggles Share my location — green when on, muted when off. It requests **when-in-use** permission, then reads GPS.
- Coords shown on the map status chip / Settings are rounded (~4 decimal places) for a privacy-friendly display.
- While sharing is on **and** you are in a family, the app writes your live position to Firebase Realtime Database under `families/{code}/locations/{uid}` (foreground only).
- Turning sharing **Off**, leaving the family, or signing out removes your published location.
- Sign-out (smaller **Out** FAB) turns sharing off again for a clean next session.
- Family create/join/leave, privacy notes, and account email live under **Settings**.

### Permissions

| Platform | What we declare |
|----------|-----------------|
| Android | `ACCESS_COARSE_LOCATION`, `ACCESS_FINE_LOCATION` (foreground / when-in-use only — no background location yet) |
| iOS / Mac Catalyst | `NSLocationWhenInUseUsageDescription` only (no Always) |

## Family groups

Privacy-first: **location is only uploaded when that user has Share my location ON.**

### How to create / join (testing)

1. Sign in as user A → **Settings** → **Create family**. Note the **6-character code**.
2. Sign in as user B (second device/emulator or after sign-out) → **Settings** → enter the code → **Join family**.
3. Both users tap the map **pin FAB** to turn sharing On and grant when-in-use permission.
4. Stay on the Map — you should see yourself and the other sharing member(s). Tap a marker to lock/follow (neighbourhood zoom ~16).

### Data model (Realtime Database)

```
users/{uid}/familyId
users/{uid}/displayName
families/{familyId}/createdBy, createdAt, code
families/{familyId}/members/{uid} = { displayName, joinedAt }
families/{familyId}/locations/{uid} = { lat, lon, updatedAt, displayName, sharing: true }
```

`familyId` is the invite code (6 chars). The client only writes its own `users/{uid}` and `…/locations/{uid}` / `…/members/{uid}` nodes.

## Firebase console steps (required)

This release uses **Firebase Realtime Database** REST with the Auth ID token (no Firestore package — keeps Android Release merges reliable). `google-services.json` already includes:

`https://whereuat-firebase-default-rtdb.asia-southeast1.firebasedatabase.app`

### Enable Realtime Database

1. Open [Firebase Console](https://console.firebase.google.com/) → project **whereuat-firebase**.
2. Build → **Realtime Database** → Create database (region **asia-southeast1** if prompted to match the URL above).
3. Start in **locked mode**, then paste the rules below.

### Starter security rules (paste in RTDB Rules)

```json
{
  "rules": {
    "users": {
      "$uid": {
        ".read": "auth != null && auth.uid == $uid",
        ".write": "auth != null && auth.uid == $uid"
      }
    },
    "families": {
      "$familyId": {
        ".read": "auth != null",
        ".write": "auth != null",
        "members": {
          "$uid": {
            ".write": "auth != null && auth.uid == $uid"
          }
        },
        "locations": {
          "$uid": {
            ".write": "auth != null && auth.uid == $uid"
          }
        }
      }
    }
  }
}
```

**Production should tighten these rules** so only members of a family may read that family’s `locations` / `members` (the starter rules above allow any signed-in user to read any family). The app already only writes the signed-in user’s own location and membership nodes, and sends the Auth ID token in the `Authorization: Bearer` header (never in the URL).

> If you prefer Firestore instead: enable Firestore in the console and mirror the same collections (`users`, `families/{id}/members`, `families/{id}/locations`). This app build talks to **Realtime Database**, not Firestore.

## Maps (OpenStreetMap)

The map UI uses a **WebView** with **Leaflet** and free **OpenStreetMap** tiles (`Resources/Raw/map.html`). No Google Maps API key is required.

- Self pin + family markers when locations are available.
- Default / lock zoom is **16** (neighbourhood/street). Subsequent updates pan without resetting to world view; if zoom &lt; 14 while locked, it bumps back to 16.
- Tap a marker to lock/follow; Unlock on the bottom bar releases follow.
- Leaflet JS/CSS load from the unpkg CDN; OSM tiles need network access.

## Privacy defaults (this release)

- Credentials / tokens are never logged; Auth ID tokens are sent only as `Authorization: Bearer` over HTTPS (not in query strings).
- Login failures show a generic message (no raw exception text).
- Precise location is never logged.
- Location is not requested until the user turns sharing on.
- No background / Always location.
- Location upload only while sharing is ON and the user is in a family; cleared on share-off / leave / sign-out.
- Family invite codes are validated (length + charset) before join.
- Map WebView loads packaged HTML only; top-level http(s) navigations are blocked.

## Note about repo docs

`Application for access to health information.docx` is unrelated documentation left in the repository; it is not part of the app.

## Next up

- Tighten RTDB rules so only family members can read a family’s locations
- Background tracking only if product explicitly requires it

## Android release APK (Obtainium)

Release builds are signed with a local keystore at `keystore/whereuat-release.jks` (gitignored). Set:

```bash
export WHEREUAT_KEYSTORE_PASS=...
export WHEREUAT_KEY_PASS=...
dotnet publish -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Package id: `com.familytracker.whereuat`. Install via [Obtainium](https://github.com/ImranR98/Obtainium) from GitHub Releases.
