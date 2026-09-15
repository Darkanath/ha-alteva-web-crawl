using System;
using System.Security.Cryptography;
using System.Text;

namespace Alteva.Domain.Services;

/// <summary>
/// Fixed-size identity for a normalized URL, used in unique indexes instead of the URL itself
/// (SQL Server caps index keys at 1700 bytes, and URL columns are case-insensitive by collation).
/// </summary>
public static class UrlHasher
{
    public const int HashLength = 32;

    /// <summary>
    /// SHA-256 over the URL's UTF-16LE bytes - byte-for-byte what SQL Server's
    /// <c>HASHBYTES('SHA2_256', nvarcharColumn)</c> produces, so rows can be hashed in SQL too.
    /// Case-sensitive: callers must pass the normalized URL.
    /// </summary>
    public static byte[] Hash(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return SHA256.HashData(Encoding.Unicode.GetBytes(url));
    }
}
