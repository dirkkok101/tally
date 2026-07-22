using System.Security.Cryptography;

namespace Tally.Domain.Ledger;

public readonly record struct LedgerId
{
    public const string InvalidIdentifierError = "identifier.invalid";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private LedgerId(string value) => Value = value;

    public string Value { get; }

    public static LedgerId New()
    {
        Span<byte> bytes = stackalloc byte[16];
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;
        RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> result = stackalloc char[26];
        for (var character = 0; character < result.Length; character++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                var sourceBit = character * 5 + bit - 2;
                if (sourceBit >= 0)
                {
                    value = (value << 1) | ((bytes[sourceBit / 8] >> (7 - sourceBit % 8)) & 1);
                }
                else value <<= 1;
            }

            result[character] = Alphabet[value];
        }
        return new LedgerId(new string(result));
    }

    public static bool TryParse(string? value, out LedgerId identifier, out string? error)
    {
        if (value is { Length: 26 } && value[0] <= '7' && HasOnlyAlphabetCharacters(value))
        {
            identifier = new LedgerId(value);
            error = null;
            return true;
        }

        identifier = default;
        error = InvalidIdentifierError;
        return false;
    }

    private static bool HasOnlyAlphabetCharacters(string value)
    {
        foreach (var character in value)
        {
            if (Alphabet.IndexOf(character) < 0) return false;
        }
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
