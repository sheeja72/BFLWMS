using System.Text.RegularExpressions;

namespace Wms.Core.Validation;

/// <summary>
/// Strict itemcode validator for the LPM Manual Building scan input.
/// Rules confirmed by user 2026-04-25:
///   1. Strip leading zeros after read.
///   2. Trim whitespace.
///   3. Reject if starts with HTTP (case-insensitive) — guards against scanned URLs.
///   4. Reject if starts with or contains ]C1 — GS1-128 FNC1 symbology identifier.
///   5. Reject if length > 15 chars (post-strip).
///   6. Reject any character outside [A-Za-z0-9-]. Only dash allowed.
/// </summary>
public static class ItemCodeValidator
{
    public const int MaxLength = 15;
    private static readonly Regex AllowedChars = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled);

    public record Result(bool IsValid, string Normalized, string? Error)
    {
        public static Result Ok(string s) => new(true, s, null);
        public static Result Bad(string s, string err) => new(false, s, err);
    }

    public static Result Validate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Bad("", "Item code is required.");

        var s = raw.Trim();

        // Reject before stripping zeros so scanner-config noise is caught first.
        if (s.Contains(']') && s.Contains("]C1", StringComparison.OrdinalIgnoreCase))
            return Result.Bad(s, "Scanner is sending GS1-128 prefix (]C1). Reconfigure scanner to strip symbology identifier.");

        if (s.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase))
            return Result.Bad(s, "Scanned value looks like a URL — not a valid item code.");

        // Strip leading zeros (preserve the digit '0' if the whole thing is zeros).
        var stripped = s.TrimStart('0');
        if (stripped.Length == 0) stripped = "0";

        if (stripped.Length > MaxLength)
            return Result.Bad(stripped, $"Item code exceeds {MaxLength} chars (got {stripped.Length}).");

        if (stripped.Contains(' '))
            return Result.Bad(stripped, "Item code cannot contain spaces.");

        if (!AllowedChars.IsMatch(stripped))
        {
            var bad = stripped.FirstOrDefault(c => !(char.IsLetterOrDigit(c) || c == '-'));
            return Result.Bad(stripped, $"Item code contains invalid character '{bad}'. Only letters, digits, and '-' are allowed.");
        }

        return Result.Ok(stripped);
    }
}

/// <summary>
/// Parses a physical box sticker like "AEINT6078-406330/001/010".
/// Format: CONTAINER + '-' + PO + '/' + SEQ + '/' + COUNT
/// If no '-' is present, PO is not available (NA).
/// </summary>
public static class BoxLabelParser
{
    private static readonly Regex Format = new(@"^(?<cont>[A-Z]+\d+)-(?<po>\d+)(/\d+){0,2}$", RegexOptions.Compiled);

    public record Parsed(string Contno, string? PoNumber, bool HasPo);

    public static Parsed Parse(string boxLabelOrNumber)
    {
        if (string.IsNullOrWhiteSpace(boxLabelOrNumber))
            return new Parsed("", null, false);

        var s = boxLabelOrNumber.Trim();
        if (!s.Contains('-'))
            return new Parsed(s, null, false);

        var m = Format.Match(s);
        if (!m.Success)
        {
            // Fall back: split on first dash, take container half. PO unknown.
            var dash = s.IndexOf('-');
            return new Parsed(s[..dash], null, false);
        }
        return new Parsed(m.Groups["cont"].Value, m.Groups["po"].Value, true);
    }
}
