using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Identity;
using Tally.Domain.Ingest.Normalization;

namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-FORMAT-ADAPTERS — Layout B proven rule (OQ-INGEST-19)
public sealed class PdfTextLayoutBStatementAdapter : IStatementAdapter
{
    private const string VariantId = "pdf-text-layout-b-v1";
    private const string AdapterVersion = "1.0.0";
    private const double BaselineTolerance = 0.1d;
    private const double DescriptionVerticalTolerance = 5d;
    private const double DateToDetailsFraction = 0.33d;
    private const double DetailsToAmountFraction = 0.75d;

    private static readonly Regex FullDate = new(
        @"\b(?<day>\d{1,2})\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(?<year>\d{4})\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex FullDateLeading = new(
        @"^\s*(?<day>\d{1,2})\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(?<year>\d{4})\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex YearlessLeading = new(
        @"^\s*(?<day>\d{1,2})(?:[/\-.](?<monthnum>\d{1,2})|\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec))\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex SignedMonetaryToken = new(
        @"(?<sign>[-+])?\s*(?:R\s*)?(?<amount>(?:\d{1,3}(?:[ ,]\d{3})*|\d+)[.,]\d{2})(?:\s*(?<direction>Cr|Dr))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AnyMonetaryToken = new(
        @"(?<sign>[-+])?\s*(?:R\s*)?(?<amount>(?:\d{1,3}(?:[ ,]\d{3})*|\d+)[.,]\d{2})(?:\s*(?:Cr|Dr))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public FormatVariantDescriptor Descriptor { get; } = new(
        VariantId,
        AdapterVersion,
        "pdfpig-0.1.14-v1",
        1,
        "application/pdf",
        PdfExtractionLimits.PrivateFixture);

    public VariantProbeResult Probe(PdfDocumentEvidence evidence)
    {
        if (!TryBuild(evidence, AccountPlaceholder(), out _, out var codes) || codes.Count == 0)
        {
            return new(Descriptor.VariantId, VariantProbeOutcome.NoMatch, []);
        }

        return new(Descriptor.VariantId, VariantProbeOutcome.ExactMatch, codes);
    }

    public ExtractedStatement Extract(PdfDocumentEvidence evidence, AccountDetail selectedAccount)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(selectedAccount);
        if (selectedAccount.Status != AccountStatus.Active || !selectedAccount.CurrencyCode.Equals("ZAR", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("INGEST-LAYOUT-B-ACCOUNT-MISMATCH");
        }

        if (!TryBuild(evidence, selectedAccount, out var statement, out _) || statement is null)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-B-NO-MATCH");
        }

        return statement;
    }

    private bool TryBuild(
        PdfDocumentEvidence evidence,
        AccountDetail selectedAccount,
        out ExtractedStatement? statement,
        out List<string> codes)
    {
        statement = null;
        codes = [];
        if (evidence.Pages.Count == 0)
        {
            return false;
        }

        var managed = FlattenManagedLines(evidence);
        if (managed.Count == 0)
        {
            return false;
        }

        var headers = new List<HeaderGeometry>();
        foreach (var page in evidence.Pages.OrderBy(page => page.PageNumber))
        {
            if (!TryFindSingleHeader(page, out var header))
            {
                return false;
            }

            headers.Add(header);
        }

        var period = TryExtractPeriod(managed);
        if (period is null)
        {
            return false;
        }

        if (!TryExtractControls(managed, out var opening, out var closing))
        {
            return false;
        }

        var metadataFields = MetadataFields(managed);
        if (metadataFields.Count == 0)
        {
            return false;
        }

        var records = new List<SourceRecordEvidence>();
        var ordinal = 0;
        foreach (var page in evidence.Pages.OrderBy(page => page.PageNumber))
        {
            var header = headers.Single(candidate => candidate.PageNumber == page.PageNumber);
            foreach (var line in page.ManagedLines.OrderBy(line => line.BlockOrder).ThenBy(line => line.LineOrder))
            {
                var text = line.Text ?? string.Empty;
                // Final signed monetary token on the managed row is the source movement.
                var moneyMatches = SignedMonetaryToken.Matches(text);
                if (moneyMatches.Count == 0)
                {
                    continue;
                }

                var money = moneyMatches[^1];

                // Prefer an explicit leading full date; otherwise a yearless day/month that is not followed by a year.
                var fullLeading = FullDateLeading.Match(text);
                Match dateMatch;
                string transactionDate;
                if (fullLeading.Success)
                {
                    dateMatch = fullLeading;
                    if (!TryParseLeadingFullDate(fullLeading, out transactionDate))
                    {
                        return false;
                    }
                }
                else
                {
                    var yearless = YearlessLeading.Match(text);
                    if (!yearless.Success)
                    {
                        continue;
                    }

                    var after = text[yearless.Length..];
                    if (Regex.IsMatch(after, @"^\s+\d{4}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                    {
                        continue;
                    }

                    if (!TryResolveYearlessDate(yearless, period, out transactionDate))
                    {
                        // Not a qualified candidate row when the yearless date is absent from the period.
                        continue;
                    }

                    dateMatch = yearless;
                }

                if (!TryParseSignedMovement(money, out var ownerSignedMinor))
                {
                    return false;
                }

                if (!TryAssociateDescription(page, line, header, text, dateMatch, money, out var description))
                {
                    return false;
                }

                // Token is already owner-economic signed movement; map through FinancialNormalizer's account-class rule.
                var amountText = FormatMinorUnits(Math.Abs(ownerSignedMinor));
                var balanceIncreased = selectedAccount.AccountClass == AccountClass.Asset
                    ? ownerSignedMinor > 0
                    : ownerSignedMinor < 0;
                var financialEvidence = new FinancialEvidence(
                    amountText,
                    selectedAccount.CurrencyCode,
                    description,
                    balanceIncreased,
                    transactionDate,
                    null,
                    null,
                    period);
                var rawEvidence = text.Normalize(NormalizationForm.FormC);
                var sourceRecordId = IngestIdentity.SourceRecordId(new(
                    evidence.SourceFingerprint,
                    $"p:{page.PageNumber}:b:{line.BlockOrder}:l:{line.LineOrder}:o:{ordinal}",
                    Sha256(rawEvidence),
                    "financial-evidence-v1"));
                records.Add(new(
                    sourceRecordId,
                    page.PageNumber,
                    ordinal,
                    "statement-transaction",
                    rawEvidence,
                    DescriptionEvidenceKind.SourceText,
                    null,
                    financialEvidence,
                    null,
                    null));
                ordinal++;
            }
        }

        if (records.Count == 0)
        {
            return false;
        }

        var ownerMovementSum = records.Sum(record =>
        {
            var amount = ParseUnsignedMinor(record.FinancialEvidence.Amount!);
            var balanceIncreased = record.FinancialEvidence.BalanceIncreased == true;
            var balanceMovement = balanceIncreased ? amount : -amount;
            return selectedAccount.AccountClass == AccountClass.Asset
                ? balanceMovement
                : -balanceMovement;
        });
        if (checked(opening + ownerMovementSum) != closing)
        {
            return false;
        }

        var metadataFingerprint = Sha256(string.Concat(AdapterVersion, "\n", string.Join("\n", metadataFields)));
        statement = new(
            Descriptor,
            period,
            new(
                selectedAccount.AccountId,
                selectedAccount.AccountClass,
                selectedAccount.CurrencyCode,
                selectedAccount.MaskedIdentifier,
                metadataFingerprint,
                true),
            records,
            opening,
            closing,
            [
                ReconciliationControlKind.OpeningBalance,
                ReconciliationControlKind.ClosingBalance,
                ReconciliationControlKind.RecordCount
            ]);
        codes =
        [
            "layout-b-managed-headers",
            "layout-b-period-dates",
            "layout-b-signed-movements",
            "layout-b-unique-controls"
        ];
        return true;
    }

    private static bool TryFindSingleHeader(PdfPageEvidence page, out HeaderGeometry header)
    {
        header = default!;
        var candidates = new List<HeaderGeometry>();
        foreach (var line in page.ManagedLines)
        {
            var text = line.Text ?? string.Empty;
            if (!ContainsWholeWord(text, "Date") ||
                !ContainsWholeWord(text, "Details") ||
                !ContainsWholeWord(text, "Amount"))
            {
                continue;
            }

            if (!TryHeaderColumns(page, line, out var dateX, out var detailsX, out var amountX))
            {
                continue;
            }

            if (!(dateX < detailsX && detailsX < amountX))
            {
                continue;
            }

            candidates.Add(new(page.PageNumber, dateX, detailsX, amountX, line.Bottom));
        }

        if (candidates.Count != 1)
        {
            return false;
        }

        header = candidates[0];
        return true;
    }

    private static bool TryHeaderColumns(
        PdfPageEvidence page,
        PdfManagedLineEvidence line,
        out double dateX,
        out double detailsX,
        out double amountX)
    {
        dateX = detailsX = amountX = 0;
        var lineGlyphs = page.OrderedGlyphs
            .Where(glyph => Math.Abs(glyph.BaselineY - line.Bottom) <= 2d ||
                            (glyph.Bottom <= line.Top + 1d && glyph.Top >= line.Bottom - 1d &&
                             glyph.Left >= line.Left - 1d && glyph.Right <= line.Right + 1d))
            .OrderBy(glyph => glyph.Left)
            .ThenBy(glyph => glyph.TextSequence)
            .ThenBy(glyph => glyph.ContentOrder)
            .ToArray();
        if (lineGlyphs.Length == 0)
        {
            lineGlyphs = page.OrderedGlyphs
                .Where(glyph => Math.Abs(glyph.BaselineY - line.Bottom) <= DescriptionVerticalTolerance)
                .OrderBy(glyph => glyph.Left)
                .ThenBy(glyph => glyph.TextSequence)
                .ToArray();
        }

        if (!TryFindTokenLeft(lineGlyphs, "Date", out dateX) ||
            !TryFindTokenLeft(lineGlyphs, "Details", out detailsX) ||
            !TryFindTokenLeft(lineGlyphs, "Amount", out amountX))
        {
            return false;
        }

        return true;
    }

    private static bool TryFindTokenLeft(IReadOnlyList<PdfGlyphEvidence> glyphs, string token, out double left)
    {
        left = 0;
        if (glyphs.Count == 0)
        {
            return false;
        }

        var builder = new StringBuilder();
        var starts = new List<double>();
        foreach (var glyph in glyphs)
        {
            starts.Add(glyph.Left);
            builder.Append(glyph.Value);
        }

        var haystack = builder.ToString();
        var index = haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || index >= starts.Count)
        {
            // Map by cumulative character positions when multi-char glyphs exist.
            var cursor = 0;
            for (var glyphIndex = 0; glyphIndex < glyphs.Count; glyphIndex++)
            {
                var value = glyphs[glyphIndex].Value;
                if (cursor <= index && index < cursor + value.Length)
                {
                    left = glyphs[glyphIndex].Left;
                    return true;
                }

                cursor += value.Length;
            }

            return false;
        }

        left = starts[Math.Min(index, starts.Count - 1)];
        // Prefer the glyph whose cumulative start matches index.
        var running = 0;
        for (var glyphIndex = 0; glyphIndex < glyphs.Count; glyphIndex++)
        {
            if (running == index)
            {
                left = glyphs[glyphIndex].Left;
                return true;
            }

            running += glyphs[glyphIndex].Value.Length;
            if (running > index)
            {
                left = glyphs[glyphIndex].Left;
                return true;
            }
        }

        return true;
    }

    private static bool TryAssociateDescription(
        PdfPageEvidence page,
        PdfManagedLineEvidence row,
        HeaderGeometry header,
        string managedRowText,
        Match dateMatch,
        Match moneyMatch,
        out string description)
    {
        description = string.Empty;
        var bandLeft = header.DateX + (DateToDetailsFraction * (header.DetailsX - header.DateX));
        var bandRight = header.DetailsX + (DetailsToAmountFraction * (header.AmountX - header.DetailsX));
        if (!(bandLeft < bandRight) || !dateMatch.Success || !moneyMatch.Success)
        {
            return false;
        }

        // Containment uses FormKC + invariant lower + non-alphanumeric stripping so glyph spacing
        // differences cannot invent a description that is not present in the managed row text.
        var compactRow = CompactComparable(managedRowText);
        var bands = GroupGlyphsByBaseline(
                page.OrderedGlyphs.Where(glyph =>
                    glyph.Left + ((glyph.Right - glyph.Left) / 2d) >= bandLeft &&
                    glyph.Left + ((glyph.Right - glyph.Left) / 2d) <= bandRight))
            .Select(group => new
            {
                Bottom = group.Average(glyph => glyph.BaselineY),
                Text = string.Concat(group
                    .OrderBy(glyph => glyph.Left)
                    .ThenBy(glyph => glyph.TextSequence)
                    .ThenBy(glyph => glyph.ContentOrder)
                    .Select(glyph => glyph.Value))
            })
            .Where(band => !string.IsNullOrWhiteSpace(band.Text))
            .Where(band => Math.Abs(band.Bottom - row.Bottom) <= DescriptionVerticalTolerance)
            .Where(band => compactRow.Contains(CompactComparable(band.Text), StringComparison.Ordinal))
            .Select(band => CollapseWhitespace(band.Text))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (bands.Length != 1)
        {
            return false;
        }

        // Emit the managed-line description span (date prefix through final monetary token exclusive).
        // The glyph band proves unique association; managed text supplies source spacing/punctuation.
        if (moneyMatch.Index < dateMatch.Length)
        {
            return false;
        }

        description = CollapseWhitespace(managedRowText[dateMatch.Length..moneyMatch.Index]);
        return description.Length > 0 && CompactComparable(description) == CompactComparable(bands[0]);
    }

    private static IEnumerable<List<PdfGlyphEvidence>> GroupGlyphsByBaseline(IEnumerable<PdfGlyphEvidence> glyphs)
    {
        var groups = new List<List<PdfGlyphEvidence>>();
        foreach (var glyph in glyphs.OrderBy(glyph => glyph.BaselineY).ThenBy(glyph => glyph.Left).ThenBy(glyph => glyph.TextSequence))
        {
            var group = groups.FirstOrDefault(candidate => Math.Abs(candidate[0].BaselineY - glyph.BaselineY) <= BaselineTolerance);
            if (group is null)
            {
                group = [];
                groups.Add(group);
            }

            group.Add(glyph);
        }

        return groups;
    }

    private static StatementPeriod? TryExtractPeriod(IReadOnlyList<ManagedLineRef> managed)
    {
        // Full dates with year, in content order, taken from period-bearing managed lines only.
        // Exactly two distinct values define the statement period (OQ-INGEST-19).
        var orderedUnique = new List<DateOnly>();
        foreach (var line in managed)
        {
            if (!line.Text.Contains("period", StringComparison.OrdinalIgnoreCase) &&
                !(line.Text.Contains("from", StringComparison.OrdinalIgnoreCase) &&
                  Regex.IsMatch(line.Text, @"\bto\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)))
            {
                continue;
            }

            foreach (Match match in FullDate.Matches(line.Text))
            {
                if (!TryParseFullDate(match, out var date))
                {
                    continue;
                }

                if (!orderedUnique.Contains(date))
                {
                    orderedUnique.Add(date);
                }
            }
        }

        if (orderedUnique.Count != 2)
        {
            return null;
        }

        var start = orderedUnique[0] <= orderedUnique[1] ? orderedUnique[0] : orderedUnique[1];
        var end = orderedUnique[0] <= orderedUnique[1] ? orderedUnique[1] : orderedUnique[0];
        return new(
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static bool TryExtractControls(IReadOnlyList<ManagedLineRef> managed, out long opening, out long closing)
    {
        opening = closing = 0;
        var openings = new HashSet<long>();
        var closings = new HashSet<long>();
        foreach (var line in managed)
        {
            var text = line.Text;
            var isOpening = text.Contains("opening", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("balance", StringComparison.OrdinalIgnoreCase);
            var isClosing = text.Contains("closing", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("balance", StringComparison.OrdinalIgnoreCase);
            if (!isOpening && !isClosing)
            {
                continue;
            }

            var moneyMatches = SignedMonetaryToken.Matches(text);
            if (moneyMatches.Count == 0)
            {
                continue;
            }

            var money = moneyMatches[^1];
            if (!TryParseSignedMovement(money, out var economicMinor))
            {
                continue;
            }

            // Final signed control token is already owner-economic (same sign convention as movements).
            if (isOpening)
            {
                openings.Add(economicMinor);
            }

            if (isClosing)
            {
                closings.Add(economicMinor);
            }
        }

        // Controls must each resolve to one unique economic value (repeat lines with the same value are allowed).
        if (openings.Count != 1 || closings.Count != 1)
        {
            return false;
        }

        opening = openings.First();
        closing = closings.First();
        return true;
    }

    private static IReadOnlyList<string> MetadataFields(IReadOnlyList<ManagedLineRef> managed)
    {
        var fields = new List<string>();
        foreach (var line in managed)
        {
            var withoutMoney = AnyMonetaryToken.Replace(line.Text, string.Empty);
            var normalized = CollapseWhitespace(withoutMoney.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (normalized.Contains("account", StringComparison.Ordinal) ||
                normalized.Contains("card", StringComparison.Ordinal))
            {
                fields.Add(normalized);
            }
        }

        return fields;
    }

    private static bool TryParseLeadingFullDate(Match fullLeading, out string transactionDate)
    {
        transactionDate = string.Empty;
        if (!DateOnly.TryParseExact(
                $"{fullLeading.Groups["day"].Value} {fullLeading.Groups["month"].Value} {fullLeading.Groups["year"].Value}",
                "d MMM yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var date))
        {
            return false;
        }

        transactionDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryResolveYearlessDate(Match yearless, StatementPeriod period, out string transactionDate)
    {
        transactionDate = string.Empty;
        var start = DateOnly.ParseExact(period.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(period.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!int.TryParse(yearless.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        var matches = new List<DateOnly>();
        if (yearless.Groups["monthnum"].Success)
        {
            if (!int.TryParse(yearless.Groups["monthnum"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                month is < 1 or > 12)
            {
                return false;
            }

            for (var year = start.Year; year <= end.Year; year++)
            {
                if (DateOnly.TryParseExact($"{year:D4}-{month:D2}-{day:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var candidate) &&
                    candidate >= start && candidate <= end)
                {
                    matches.Add(candidate);
                }
            }
        }
        else
        {
            var monthText = yearless.Groups["month"].Value;
            for (var year = start.Year; year <= end.Year; year++)
            {
                if (DateOnly.TryParseExact($"{day} {monthText} {year}", "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var candidate) &&
                    candidate >= start && candidate <= end)
                {
                    matches.Add(candidate);
                }
            }
        }

        if (matches.Count != 1)
        {
            return false;
        }

        transactionDate = matches[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseSignedMovement(Match money, out long movementMinor)
    {
        movementMinor = 0;
        var amountGroup = money.Groups["amount"].Success ? money.Groups["amount"].Value : money.Value;
        amountGroup = amountGroup.Replace("R", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        // Normalize thousand separators and decimal commas.
        if (amountGroup.Contains(',', StringComparison.Ordinal) && amountGroup.Contains('.', StringComparison.Ordinal))
        {
            amountGroup = amountGroup.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (amountGroup.Contains(',', StringComparison.Ordinal) && Regex.IsMatch(amountGroup, @"^\d+,\d{2}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            amountGroup = amountGroup.Replace(',', '.');
        }
        else
        {
            amountGroup = amountGroup.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        amountGroup = amountGroup.Trim().TrimStart('+', '-');
        if (!decimal.TryParse(amountGroup, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || decimal.Truncate(amount * 100m) != amount * 100m || amount > long.MaxValue / 100m)
        {
            return false;
        }

        var minor = checked((long)(amount * 100m));
        var negative =
            (money.Groups["sign"].Success && money.Groups["sign"].Value == "-") ||
            (money.Groups["direction"].Success && money.Groups["direction"].Value.Equals("Dr", StringComparison.OrdinalIgnoreCase)) ||
            money.Value.TrimStart().StartsWith('-');
        if (money.Groups["direction"].Success && money.Groups["direction"].Value.Equals("Cr", StringComparison.OrdinalIgnoreCase))
        {
            negative = false;
        }

        movementMinor = negative ? -minor : minor;
        return true;
    }

    private static bool TryParseFullDate(Match match, out DateOnly date)
    {
        date = default;
        var text = match.Value;
        string[] formats = ["d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "dd MMM yyyy"];
        return DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static List<ManagedLineRef> FlattenManagedLines(PdfDocumentEvidence evidence)
    {
        var lines = new List<ManagedLineRef>();
        foreach (var page in evidence.Pages.OrderBy(page => page.PageNumber))
        {
            foreach (var line in page.ManagedLines.OrderBy(line => line.BlockOrder).ThenBy(line => line.LineOrder))
            {
                lines.Add(new(page.PageNumber, line.BlockOrder, line.LineOrder, line.Text ?? string.Empty, line.Bottom));
            }
        }

        return lines;
    }

    private static bool ContainsWholeWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static string CollapseWhitespace(string value) => Whitespace.Replace(value.Trim(), " ");

    private static string NormalizeComparable(string value) =>
        CollapseWhitespace(value.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();

    private static string CompactComparable(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string FormatMinorUnits(long value) =>
        (value / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static long ParseUnsignedMinor(string value) =>
        checked((long)(decimal.Parse(value, CultureInfo.InvariantCulture) * 100m));

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static AccountDetail AccountPlaceholder() => new(
        "probe",
        "institution",
        "display",
        AccountType.Cheque,
        AccountClass.Asset,
        "masked",
        "ZAR",
        AccountStatus.Active,
        "actor",
        "2026-01-01T00:00:00Z",
        null,
        []);

    private sealed record HeaderGeometry(int PageNumber, double DateX, double DetailsX, double AmountX, double Bottom);

    private sealed record ManagedLineRef(int PageNumber, int BlockOrder, int LineOrder, string Text, double Bottom);
}
