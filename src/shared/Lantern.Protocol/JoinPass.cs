using System.Security.Cryptography;
using System.Text;

namespace Lantern.Protocol;

/// <summary>
/// The join pass a player's Lantern app sends to a passworded server when it connects
/// (POST /api/v1/join/pass on the server's HTTP port). The request carries a timestamp and
/// a random nonce; the X-Lantern-Join-Proof header carries
/// HMAC-SHA256(server password, <see cref="Canonical"/>) as lowercase hex. The password
/// itself never leaves the player's machine.
///
/// The server checks the proof and answers with a random pass token, valid for
/// <see cref="LifetimeSeconds"/>. The app's join mod presents the token over its own game
/// connection (<see cref="GameMessagePrefix"/> + token), so the host knows which player it
/// belongs to; a player with no valid token is sent back to the menu. One canonical form,
/// used by both sides, so the launcher and the server cannot drift apart.
/// </summary>
public static class JoinPass
{
    public const int Version = 1;
    public const string Route = "join/pass";
    public const string ProofHeader = "X-Lantern-Join-Proof";

    /// <summary>The file in the app's data folder the join mod reads the current token from.</summary>
    public const string TokenFileName = "join-pass.txt";

    /// <summary>What the join mod sends over the game connection, followed by the token.</summary>
    public const string GameMessagePrefix = "lantern-pass ";

    /// <summary>How long a recorded pass stays valid without a refresh.</summary>
    public const int LifetimeSeconds = 300;

    /// <summary>How far a request's timestamp may be from the server's clock.</summary>
    public const int MaxClockSkewSeconds = 300;

    /// <summary>
    /// The signed text. The "pass" prefix keeps a pass proof from ever matching the
    /// character-choice proof, which signs a different line layout with the same password.
    /// </summary>
    public static string Canonical(int version, long timestamp, string nonce) =>
        $"pass\n{version}\n{timestamp}\n{nonce}";

    /// <summary>Lowercase hex HMAC-SHA256 of <paramref name="canonical"/> keyed by the password.</summary>
    public static string Proof(string password, string canonical) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>A fresh 32-character lowercase hex nonce.</summary>
    public static string NewNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
