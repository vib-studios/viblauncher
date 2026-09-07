using System.Security.Cryptography;
using System.Text;

namespace VibLauncher.Core.Accounts;

public enum AccountKind
{
    /// <summary>A local profile. Not authenticated with Mojang and not usable on online-mode servers.</summary>
    Offline,

    /// <summary>A Microsoft account that has completed the Xbox Live and Minecraft token exchange.</summary>
    Microsoft,
}

/// <summary>
/// A profile the launcher can start Minecraft with.
/// </summary>
/// <remarks>
/// No token material lives on this type. Access and refresh tokens are held by
/// <see cref="ITokenStore"/> and keyed by <see cref="Id"/>, so the account list
/// can be serialised to plain JSON without ever writing a credential to disk.
/// </remarks>
public sealed class Account
{
    /// <summary>Launcher-local identifier. Stable across renames and re-authentication.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>The in-game name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The Minecraft profile UUID, without dashes.</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>When the Minecraft access token expires. Only meaningful for Microsoft accounts.</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// True when this is a Microsoft account whose Minecraft token has expired or
    /// is close enough to expiry that it should be refreshed before launching.
    /// </summary>
    public bool NeedsRefresh =>
        Kind == AccountKind.Microsoft
        && (TokenExpiresAt is null || TokenExpiresAt.Value - DateTimeOffset.Now < TimeSpan.FromMinutes(5));

    /// <summary>How the account type reads in the UI. Offline accounts are never dressed up as authenticated ones.</summary>
    public string KindLabel => Kind == AccountKind.Microsoft ? "Microsoft" : "Offline";

    /// <summary>
    /// Derives the UUID that a server assigns to an unauthenticated player.
    /// </summary>
    /// <remarks>
    /// Minecraft servers in offline mode compute a version 3 (MD5) UUID over the
    /// bytes of <c>OfflinePlayer:&lt;name&gt;</c>. Reproducing it here means a
    /// local profile keeps the same player data across launches and matches what
    /// a vib-MC server running with <c>online-mode=false</c> will hand out.
    /// </remarks>
    public static string OfflineUuid(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        // Stamp the version (3) and the RFC 4122 variant into the hash, exactly as
        // Java's UUID.nameUUIDFromBytes does.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Formats <see cref="Uuid"/> in the dashed form Minecraft's arguments expect.</summary>
    public string DashedUuid =>
        Uuid.Length == 32
            ? $"{Uuid[..8]}-{Uuid[8..12]}-{Uuid[12..16]}-{Uuid[16..20]}-{Uuid[20..]}"
            : Uuid;
}
