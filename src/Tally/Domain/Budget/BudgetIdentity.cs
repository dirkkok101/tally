using System.Security.Cryptography;

namespace Tally.Domain.Budget;

/// <summary>
/// ULID-compatible stable identifiers for BUDGET plans, revisions, events, and category id shape checks.
/// Independent of LEDGER domain types (DD-BUDGET-LEDGER-PUBLIC-COMPOSITION).
/// </summary>
public readonly record struct BudgetIdentity
{
    public const string InvalidIdentifierError = "identifier.invalid";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private BudgetIdentity(string value) => Value = value;

    public string Value { get; }

    public static BudgetIdentity New(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
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
                else
                {
                    value <<= 1;
                }
            }

            result[character] = Alphabet[value];
        }

        return new BudgetIdentity(new string(result));
    }

    public static bool TryParse(string? value, out BudgetIdentity identifier, out string? error)
    {
        if (value is { Length: 26 } && value[0] <= '7' && HasOnlyAlphabetCharacters(value))
        {
            identifier = new BudgetIdentity(value);
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
            if (Alphabet.IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
