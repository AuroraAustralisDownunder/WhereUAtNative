using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace WhereUAtNative.Services;

/// <summary>
/// Family + location sync over Firebase Realtime Database REST (Auth ID token).
/// Avoids Plugin.Firebase.Firestore native merge issues on Android Release.
/// </summary>
public sealed class FirebaseFamilyService : IFamilyService, IDisposable
{
    public const string FamilyIdPreferenceKey = "family_id";

    // From Platforms/Android/google-services.json project_info.firebase_url
    private const string DatabaseUrl =
        "https://whereuat-firebase-default-rtdb.asia-southeast1.firebasedatabase.app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAuthService _auth;
    private readonly HttpClient _http;
    private string? _familyId;

    public FirebaseFamilyService(IAuthService auth)
    {
        _auth = auth;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            _familyId = Preferences.Default.Get(FamilyIdPreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(_familyId))
                _familyId = null;
        }
        catch
        {
            // Preferences may not be ready during early CreateMauiApp DI resolve.
            _familyId = null;
        }
    }

    public string? CurrentFamilyId => _familyId;

    public event EventHandler? FamilyChanged;

    public async Task RefreshMembershipAsync(CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        if (string.IsNullOrEmpty(uid))
        {
            SetFamilyId(null);
            return;
        }

        try
        {
            var json = await GetAsync($"users/{uid}/familyId.json", cancellationToken);
            var remote = ParseStringValue(json);
            SetFamilyId(string.IsNullOrWhiteSpace(remote) ? null : remote);
        }
        catch
        {
            // Keep cached preference on transient network errors.
        }
    }

    public async Task<(string? Code, string? Error)> CreateFamilyAsync(CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        if (string.IsNullOrEmpty(uid))
            return (null, "Sign in to create a family.");

        if (!string.IsNullOrEmpty(_familyId))
            return (null, "Leave your current family before creating a new one.");

        try
        {
            // Under member-only family read rules we cannot probe families/{code} before create.
            // Create must PUT the family root WITH members/{uid} in the same write so
            // !data.exists() && newData.child('members').child(auth.uid).exists() passes.
            // Collisions (code taken) fail the write; retry with a new code.
            // Permission/auth failures are not collisions — surface them immediately.
            Exception? lastTransient = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var code = GenerateFamilyCode();
                var displayName = _auth.DisplayName;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var familyPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["createdBy"] = uid,
                    ["createdAt"] = now,
                    ["code"] = code,
                    ["members"] = new Dictionary<string, object?>
                    {
                        [uid] = new Dictionary<string, object?>
                        {
                            ["displayName"] = displayName,
                            ["joinedAt"] = now
                        }
                    }
                });

                try
                {
                    await PutAsync($"families/{code}.json", familyPayload, cancellationToken);
                }
                catch (RtdbHttpException http) when (IsLikelyCodeCollision(http))
                {
                    lastTransient = http;
                    continue;
                }

                var userPayload = JsonSerializer.Serialize(new
                {
                    familyId = code,
                    displayName
                });
                await PutAsync($"users/{uid}.json", userPayload, cancellationToken);

                SetFamilyId(code);
                return (code, null);
            }

            return (null, lastTransient is null
                ? "Could not allocate a family code. Try again."
                : MapCreateError(lastTransient));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, MapCreateError(ex));
        }
    }

    /// <summary>
    /// Invite-code collision: existing family makes !data.exists() fail → RTDB "Permission denied".
    /// Unauthorized (bad/missing ID token) is not a collision and must not be retried.
    /// </summary>
    private static bool IsLikelyCodeCollision(RtdbHttpException http) =>
        http.IsPermissionDenied && !http.IsUnauthorized;

    public async Task<string?> JoinFamilyAsync(string code, CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        if (string.IsNullOrEmpty(uid))
            return "Sign in to join a family.";

        code = NormalizeCode(code);
        if (!IsValidInviteCode(code))
            return "Invalid code. Enter the 6-character family invite (letters/digits, no 0/O/1/I).";

        if (!string.IsNullOrEmpty(_familyId))
            return "Leave your current family before joining another.";

        try
        {
            // Non-members cannot READ families/{code} under tightened rules.
            // Self-write on members/{uid} is allowed; join by PUT only that path,
            // then verify the family looks real (createdBy) now that we can read.
            var displayName = _auth.DisplayName;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var memberPayload = JsonSerializer.Serialize(new
            {
                displayName,
                joinedAt = now
            });
            await PutAsync($"families/{code}/members/{uid}.json", memberPayload, cancellationToken);

            string familyJson;
            try
            {
                familyJson = await GetAsync($"families/{code}.json", cancellationToken);
            }
            catch (Exception verifyEx)
            {
                await TryDeleteMemberAsync(code, uid, cancellationToken);
                var mapped = MapJoinError(verifyEx);
                return mapped.StartsWith("No network", StringComparison.Ordinal)
                    ? "Could not verify family after join (no network)."
                    : mapped;
            }

            if (IsJsonNull(familyJson) || !FamilyLooksValid(familyJson))
            {
                await TryDeleteMemberAsync(code, uid, cancellationToken);
                return "No family found for that code.";
            }

            var userPayload = JsonSerializer.Serialize(new
            {
                familyId = code,
                displayName
            });
            await PutAsync($"users/{uid}.json", userPayload, cancellationToken);

            SetFamilyId(code);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapJoinError(ex);
        }
    }

    private async Task TryDeleteMemberAsync(string familyId, string uid, CancellationToken ct)
    {
        try
        {
            await DeleteAsync($"families/{familyId}/members/{uid}.json", ct);
        }
        catch
        {
            // best-effort rollback
        }
    }

    /// <summary>True when the family node has a creator (not a ghost from a mistyped join).</summary>
    private static bool FamilyLooksValid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            return doc.RootElement.TryGetProperty("createdBy", out var createdBy) &&
                   createdBy.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(createdBy.GetString());
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> LeaveFamilyAsync(CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        var familyId = _familyId;
        if (string.IsNullOrEmpty(uid))
            return "Not signed in.";

        if (string.IsNullOrEmpty(familyId))
        {
            SetFamilyId(null);
            return null;
        }

        try
        {
            await DeleteAsync($"families/{familyId}/locations/{uid}.json", cancellationToken);
            await DeleteAsync($"families/{familyId}/members/{uid}.json", cancellationToken);
            await DeleteAsync($"users/{uid}/familyId.json", cancellationToken);
            SetFamilyId(null);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Still clear local so the user isn't stuck; remote may clean up later.
            SetFamilyId(null);
            return "Left locally; remote cleanup may have failed — check connection.";
        }
    }

    public async Task<IReadOnlyList<FamilyMemberInfo>> GetMembersAsync(CancellationToken cancellationToken = default)
    {
        var familyId = _familyId;
        if (string.IsNullOrEmpty(familyId))
            return Array.Empty<FamilyMemberInfo>();

        try
        {
            var json = await GetAsync($"families/{familyId}/members.json", cancellationToken);
            if (IsJsonNull(json))
                return Array.Empty<FamilyMemberInfo>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<FamilyMemberInfo>();

            var list = new List<FamilyMemberInfo>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var name = "Member";
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("displayName", out var dn) &&
                    dn.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(dn.GetString()))
                {
                    name = dn.GetString()!;
                }

                list.Add(new FamilyMemberInfo { Uid = prop.Name, DisplayName = name });
            }

            return list.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<FamilyMemberInfo>();
        }
    }

    public async Task PublishLocationAsync(double lat, double lon, CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        var familyId = _familyId;
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(familyId))
            return;

        if (!IsFinite(lat) || !IsFinite(lon))
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                lat,
                lon,
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                displayName = _auth.DisplayName,
                sharing = true
            });
            await PutAsync($"families/{familyId}/locations/{uid}.json", payload, cancellationToken);
        }
        catch
        {
            // Never crash UI for upload misses.
        }
    }

    public async Task ClearPublishedLocationAsync(CancellationToken cancellationToken = default)
    {
        var uid = _auth.CurrentUserId;
        var familyId = _familyId;
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(familyId))
            return;

        try
        {
            await DeleteAsync($"families/{familyId}/locations/{uid}.json", cancellationToken);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<IReadOnlyList<FamilyLocation>> GetFamilyLocationsAsync(CancellationToken cancellationToken = default)
    {
        var familyId = _familyId;
        if (string.IsNullOrEmpty(familyId))
            return Array.Empty<FamilyLocation>();

        try
        {
            var json = await GetAsync($"families/{familyId}/locations.json", cancellationToken);
            if (IsJsonNull(json))
                return Array.Empty<FamilyLocation>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<FamilyLocation>();

            var list = new List<FamilyLocation>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var obj = prop.Value;
                if (obj.TryGetProperty("sharing", out var sharingEl) &&
                    sharingEl.ValueKind == JsonValueKind.False)
                    continue;

                if (!TryGetDouble(obj, "lat", out var lat) || !TryGetDouble(obj, "lon", out var lon))
                    continue;

                var name = prop.Name;
                if (obj.TryGetProperty("displayName", out var dn) &&
                    dn.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(dn.GetString()))
                {
                    name = dn.GetString()!;
                }

                long updatedAt = 0;
                if (obj.TryGetProperty("updatedAt", out var ua) && ua.TryGetInt64(out var uaVal))
                    updatedAt = uaVal;

                list.Add(new FamilyLocation
                {
                    Uid = prop.Name,
                    DisplayName = name,
                    Latitude = lat,
                    Longitude = lon,
                    Sharing = true,
                    UpdatedAt = updatedAt
                });
            }

            return list;
        }
        catch
        {
            return Array.Empty<FamilyLocation>();
        }
    }

    private void SetFamilyId(string? familyId)
    {
        var normalized = string.IsNullOrWhiteSpace(familyId) ? null : familyId.Trim().ToUpperInvariant();
        if (_familyId == normalized)
            return;

        _familyId = normalized;
        try
        {
            if (normalized is null)
                Preferences.Default.Remove(FamilyIdPreferenceKey);
            else
                Preferences.Default.Set(FamilyIdPreferenceKey, normalized);
        }
        catch
        {
            // Preferences unavailable — keep in-memory family id only.
        }

        try { FamilyChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* subscriber fault */ }
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        var url = await BuildAuthenticatedUrlAsync(path, ct);
        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RtdbHttpException(response.StatusCode, $"GET {path} failed: {(int)response.StatusCode}", body);
        return body;
    }

    private async Task PutAsync(string path, string jsonBody, CancellationToken ct)
    {
        var url = await BuildAuthenticatedUrlAsync(path, ct);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(url, content, ct);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new RtdbHttpException(response.StatusCode, $"PUT {path} failed: {(int)response.StatusCode}", body);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        var url = await BuildAuthenticatedUrlAsync(path, ct);
        using var response = await _http.DeleteAsync(url, ct);
        // 404 is fine
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new RtdbHttpException(response.StatusCode, $"DELETE {path} failed: {(int)response.StatusCode}", body);
        }
    }

    /// <summary>
    /// Firebase ID tokens must use the <c>auth</c> query parameter on the RTDB REST API.
    /// <c>Authorization: Bearer</c> is only for Google OAuth2 access tokens (admin), not ID tokens.
    /// Transit is HTTPS so the query string is encrypted; avoid logging full URLs.
    /// </summary>
    private async Task<string> BuildAuthenticatedUrlAsync(string path, CancellationToken ct)
    {
        var token = await _auth.GetIdTokenAsync(false);
        if (string.IsNullOrEmpty(token))
            token = await _auth.GetIdTokenAsync(true);

        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Not authenticated.");

        var trimmed = path.TrimStart('/');
        return $"{DatabaseUrl}/{trimmed}?auth={Uri.EscapeDataString(token)}";
    }

    private static string MapCreateError(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("Not authenticated", StringComparison.Ordinal))
            return "Not signed in. Sign in again, then retry.";
        if (ex is HttpRequestException or TaskCanceledException)
            return "No network connection. Check Wi‑Fi/mobile data and try again.";
        if (ex is RtdbHttpException http)
        {
            if (http.IsPermissionDenied)
                return "Permission denied by Realtime Database rules. Confirm you are signed in and RTDB rules allow family create.";
            if (http.IsUnauthorized)
                return "Session expired or invalid. Sign out, sign in again, then retry.";
            return $"Could not create family (HTTP {(int)http.StatusCode}). Try again.";
        }
        return "Could not create family. Check your connection and that Realtime Database is enabled.";
    }

    private static string MapJoinError(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("Not authenticated", StringComparison.Ordinal))
            return "Not signed in. Sign in again, then retry.";
        if (ex is HttpRequestException or TaskCanceledException)
            return "No network connection. Check Wi‑Fi/mobile data and try again.";
        if (ex is RtdbHttpException http)
        {
            if (http.IsPermissionDenied)
                return "Permission denied. The code may be wrong, or Realtime Database rules blocked the join.";
            if (http.IsUnauthorized)
                return "Session expired or invalid. Sign out, sign in again, then retry.";
            return $"Could not join family (HTTP {(int)http.StatusCode}). Try again.";
        }
        return "Could not join family. Check your connection.";
    }

    private sealed class RtdbHttpException : Exception
    {
        public System.Net.HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }

        public RtdbHttpException(System.Net.HttpStatusCode statusCode, string message, string responseBody)
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }

        public bool IsPermissionDenied =>
            ResponseBody.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            StatusCode == System.Net.HttpStatusCode.Forbidden;

        public bool IsUnauthorized =>
            !IsPermissionDenied &&
            (StatusCode == System.Net.HttpStatusCode.Unauthorized ||
             ResponseBody.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));
    }

    private static string GenerateFamilyCode()
    {
        // Avoid ambiguous 0/O, 1/I
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    private static string NormalizeCode(string code)
        => new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Invite codes are 6 chars from the generation alphabet (no 0/O/1/I).</summary>
    private static bool IsValidInviteCode(string code)
    {
        if (code.Length != 6)
            return false;
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        foreach (var c in code)
        {
            if (alphabet.IndexOf(c) < 0)
                return false;
        }
        return true;
    }

    private static bool IsJsonNull(string? json)
        => string.IsNullOrWhiteSpace(json) || json.Trim() == "null";

    private static string? ParseStringValue(string? json)
    {
        if (IsJsonNull(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(json!, JsonOptions);
        }
        catch
        {
            return json?.Trim().Trim('"');
        }
    }

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el))
            return false;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return IsFinite(value);

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return IsFinite(value);

        return false;
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    public void Dispose() => _http.Dispose();
}
