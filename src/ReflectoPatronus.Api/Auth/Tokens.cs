using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ReflectoPatronus.Api.Auth;

/// <summary>Opaque bearer tokens: 256 random bits; only the SHA-256 is ever stored.</summary>
static class Tokens
{
    public static string New() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
