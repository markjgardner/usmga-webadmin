using System.Security.Cryptography;

namespace Usmga.FunctionApp.Services;

public sealed class SecureTokenGenerator : ITokenGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string NewRequestCode()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.ToArray().Select(b => Alphabet[b % Alphabet.Length]).ToArray());
    }

    public string NewNonce(int bytes = 16)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
