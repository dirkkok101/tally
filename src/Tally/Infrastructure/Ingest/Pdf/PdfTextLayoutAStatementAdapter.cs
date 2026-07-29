using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Identity;
using Tally.Domain.Ingest.Normalization;

namespace Tally.Infrastructure.Ingest.Pdf;

// DD-INGEST-FORMAT-ADAPTERS
public sealed class PdfTextLayoutAStatementAdapter : IStatementAdapter
{
    private const string VariantId = "pdf-text-layout-a-v1";
    private const string AdapterVersion = "1.0.0";
    private const string SourceDescriptionUnavailableMarker = "Description unavailable in source statement";

    private static readonly Regex StatementPeriodDate = new(
        @"\b(?<day>\d{1,2})\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<year>\d{4})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex ShortDate = new(
        @"(?<day>\d{1,2})\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex YearlessDate = new(
        @"^\s*(?<day>\d{1,2})\s+(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex MoneyWithDirection = new(
        @"(?<!\d)(?<amount>(?:\d{1,3}(?:[ ,]\d{3})*|\d+)\.\d{2})(?<direction>Cr|Dr)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LayoutAMoney = new(
        @"(?<![\p{L}\p{N}])(?:R\s*)?(?<amount>[-+]?(?:\d{1,3}(?:[ ,]\d{3})+|\d+)[,.]\d{2})(?:\s*(?<direction>Cr|Dr))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MonetaryToken = new(
        @"(?<![\p{L}\p{N}])[-+]?(?:\d{1,3}(?:[ ,]\d{3})*|\d+)\.\d{2}(?:\s*(?:Cr|Dr))?",
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
        var content = ExtractContent(evidence);
        var lines = VisualLines(evidence).ToArray();
        var rowExtraction = ExtractRowCandidates(lines);
        var hasControls = MoneyWithDirection.Matches(content).Count >= 2 &&
            lines.Any(line => line.Text.Contains("opening balance", StringComparison.OrdinalIgnoreCase)) &&
            lines.Any(line => line.Text.Contains("closing balance", StringComparison.OrdinalIgnoreCase));
        // Period is resolved from labeled / same-line pairs — additional full dates elsewhere in the
        // statement (notices, footnotes) must not disqualify an otherwise complete Layout A source.
        if (!TryExtractStatementPeriod(lines, out _) ||
            rowExtraction.Rows.Count < 2 ||
            !rowExtraction.Complete ||
            !hasControls)
        {
            return new(Descriptor.VariantId, VariantProbeOutcome.NoMatch, []);
        }

        return new(
            Descriptor.VariantId,
            VariantProbeOutcome.ExactMatch,
            ["layout-a-explicit-period", "layout-a-running-balance-transitions"]);
    }

    public ExtractedStatement Extract(PdfDocumentEvidence evidence, AccountDetail selectedAccount)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(selectedAccount);
        if (Probe(evidence).Outcome != VariantProbeOutcome.ExactMatch)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-NO-MATCH");
        }

        if (selectedAccount.Status != AccountStatus.Active || !selectedAccount.CurrencyCode.Equals("ZAR", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-ACCOUNT-MISMATCH");
        }

        var content = ExtractContent(evidence);
        var lines = VisualLines(evidence).ToArray();
        if (!TryExtractStatementPeriod(lines, out var period))
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-PERIOD");
        }

        var rows = ExtractRowCandidates(lines).Rows;
        var controls = ExtractStatementControls(content);
        var records = new List<SourceRecordEvidence>(rows.Count);
        var previousBalanceMinor = controls.OpeningBalanceMinor;
        var ordinal = 0;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var runningBalanceMinor = ParseEconomicBalance(row.Balance);
            var movementMinor = checked(runningBalanceMinor - previousBalanceMinor);
            // Yearless day/month tokens that cannot be uniquely placed inside the resolved period
            // are not statement rows (carry-forward notices, prior-period footnotes).
            if (!TryResolveDate(row.Date, period, out var date))
            {
                previousBalanceMinor = runningBalanceMinor;
                continue;
            }

            var description = CollapseWhitespace(row.Description);

            var financialEvidence = new FinancialEvidence(
                FormatMinorUnits(Math.Abs(movementMinor)),
                selectedAccount.CurrencyCode,
                description,
                movementMinor > 0,
                date,
                null,
                null,
                period);
            var rawEvidence = row.RawEvidence.Normalize(NormalizationForm.FormC);
            var sourceRecordId = IngestIdentity.SourceRecordId(new(
                evidence.SourceFingerprint,
                $"p:{row.PageNumber}:r:{ordinal}",
                Sha256(rawEvidence),
                "financial-evidence-v1"));
            records.Add(new(
                sourceRecordId,
                row.PageNumber,
                ordinal,
                "statement-transaction",
                row.RawEvidence,
                row.DescriptionEvidenceKind,
                null,
                financialEvidence,
                runningBalanceMinor,
                null));
            previousBalanceMinor = runningBalanceMinor;
            ordinal++;
        }

        if (records.Count < 2)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-NO-MATCH");
        }

        if (previousBalanceMinor != controls.ClosingBalanceMinor)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-CLOSING-CONTROL");
        }

        var metadataFingerprint = MetadataFingerprint(evidence);
        return new(
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
            controls.OpeningBalanceMinor,
            controls.ClosingBalanceMinor,
            [
                ReconciliationControlKind.OpeningBalance,
                ReconciliationControlKind.ClosingBalance,
                ReconciliationControlKind.RunningBalance,
                ReconciliationControlKind.RecordCount
            ]);
    }

    private static RowExtraction ExtractRowCandidates(IReadOnlyList<VisualLine> lines)
    {
        var headerIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var text = lines[index].Text;
            if (text.Contains("date", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("description", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("amount", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("balance", StringComparison.OrdinalIgnoreCase))
            {
                headerIndex = index;
                break;
            }
        }

        if (headerIndex < 0)
        {
            return new([], false);
        }

        var balanceTextIndex = lines[headerIndex].Text.IndexOf("balance", StringComparison.OrdinalIgnoreCase);
        var balanceLeft = HorizontalPosition(lines[headerIndex], balanceTextIndex);
        var anchors = new List<RowAnchor>();

        for (var lineIndex = headerIndex + 1; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var date = YearlessDate.Match(line.Text);
            if (!date.Success)
            {
                continue;
            }

            var money = LayoutAMoney.Matches(line.Text).Cast<Match>().ToArray();
            var transaction = money.LastOrDefault(match => HorizontalPosition(line, match.Index) < balanceLeft);
            var balance = money.FirstOrDefault(match => HorizontalPosition(line, match.Index) >= balanceLeft);
            // Skip non-row yearless-date lines (section labels, footnotes). A single incomplete
            // dated line must not abort an otherwise complete Layout A table.
            if (transaction is null || balance is null)
            {
                continue;
            }

            var description = line.Text[date.Length..transaction.Index].Trim();
            anchors.Add(new(line, balance, date, description));
        }

        if (anchors.Count < 2)
        {
            return new([], false);
        }

        var rows = new List<RowCandidate>(anchors.Count);
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var description = anchor.Description;
            var descriptionEvidenceKind = DescriptionEvidenceKind.SourceText;
            VisualLine? descriptionLine = null;
            if (description.Length == 0)
            {
                var upperBound = index == 0
                    ? anchor.Line.Bottom + ((anchor.Line.Bottom - anchors[index + 1].Line.Bottom) / 2d)
                    : (anchors[index - 1].Line.Bottom + anchor.Line.Bottom) / 2d;
                var lowerBound = index == anchors.Count - 1
                    ? anchor.Line.Bottom - ((anchors[index - 1].Line.Bottom - anchor.Line.Bottom) / 2d)
                    : (anchor.Line.Bottom + anchors[index + 1].Line.Bottom) / 2d;
                var candidates = lines.Skip(headerIndex + 1)
                    .Where(candidate => candidate != anchor.Line &&
                        candidate.PageNumber == anchor.Line.PageNumber &&
                        candidate.Bottom <= upperBound && candidate.Bottom >= lowerBound &&
                        IsDescriptionOnlyLine(candidate))
                    .ToArray();
                if (candidates.Length > 1)
                {
                    return new([], false);
                }

                if (candidates.Length == 0)
                {
                    description = SourceDescriptionUnavailableMarker;
                    descriptionEvidenceKind = DescriptionEvidenceKind.SourceAbsentMarker;
                }
                else
                {
                    descriptionLine = candidates[0];
                    description = CollapseWhitespace(descriptionLine.Text);
                }
            }

            var rawEvidence = descriptionLine is null
                ? anchor.Line.SourceText
                : descriptionLine.Bottom > anchor.Line.Bottom
                    ? string.Concat(descriptionLine.SourceText, "\n", anchor.Line.SourceText)
                    : string.Concat(anchor.Line.SourceText, "\n", descriptionLine.SourceText);
            rows.Add(new(
                anchor.Line.PageNumber,
                anchor.Balance,
                anchor.Date,
                description,
                descriptionEvidenceKind,
                rawEvidence));
        }

        return new(rows, true);
    }

    private static double HorizontalPosition(VisualLine line, int textIndex) =>
        line.Positions.First(position => position.End > textIndex).Left;

    private static bool IsDescriptionOnlyLine(VisualLine line)
    {
        var text = CollapseWhitespace(line.Text);
        return text.Length > 0 &&
            !YearlessDate.IsMatch(text) &&
            LayoutAMoney.Matches(text).Count == 0 &&
            !text.Contains("opening balance", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("closing balance", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("statement period", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the statement period from visual lines without requiring the whole document to
    /// contain exactly two full dates. Prefer a period-labeled line, then any single line with
    /// exactly two full dates. Never invent a period from opaque filenames.
    /// </summary>
    private static bool TryExtractStatementPeriod(IReadOnlyList<VisualLine> lines, out StatementPeriod period)
    {
        period = default!;

        static DateOnly[] DistinctSortedDates(string text) =>
            StatementPeriodDate.Matches(text)
                .Cast<Match>()
                .Select(ParseFullDate)
                .Distinct()
                .OrderBy(static date => date)
                .ToArray();

        static StatementPeriod ToPeriod(DateOnly start, DateOnly end) => new(
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // 1) Period-labeled lines (statement period / from-to wording).
        foreach (var line in lines)
        {
            var text = line.Text;
            if (!text.Contains("period", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("from", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var labeled = DistinctSortedDates(text);
            if (labeled.Length == 2)
            {
                period = ToPeriod(labeled[0], labeled[1]);
                return true;
            }
        }

        // 2) Any single visual line that carries exactly two full dates.
        foreach (var line in lines)
        {
            var pair = DistinctSortedDates(line.Text);
            if (pair.Length == 2)
            {
                period = ToPeriod(pair[0], pair[1]);
                return true;
            }
        }

        return false;
    }

    private static StatementControls ExtractStatementControls(string content)
    {
        var controls = MoneyWithDirection.Matches(content).Cast<Match>().Take(2).ToArray();
        if (controls.Length != 2)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-STATEMENT-CONTROLS");
        }

        return new(ParseEconomicBalance(controls[0]), ParseEconomicBalance(controls[1]));
    }

    private static DateOnly ParseFullDate(Match match)
    {
        if (!DateOnly.TryParseExact(match.Value, ["d MMMM yyyy", "dd MMMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-PERIOD-DATE");
        }

        return date;
    }

    private static bool TryResolveDate(Match match, StatementPeriod period, out string date)
    {
        date = string.Empty;
        var start = DateOnly.ParseExact(period.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(period.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        var monthText = match.Groups["month"].Value;
        var matches = new List<DateOnly>();
        for (var year = start.Year; year <= end.Year; year++)
        {
            if (DateOnly.TryParseExact($"{day} {monthText} {year}", "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var candidate) &&
                candidate >= start && candidate <= end)
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count != 1)
        {
            return false;
        }

        date = matches[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static long ParseMinorUnits(string value)
    {
        var canonical = value.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(canonical, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || decimal.Truncate(amount * 100m) != amount * 100m || amount > long.MaxValue / 100m)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-MONEY");
        }

        return checked((long)(amount * 100m));
    }

    private static long ParseEconomicBalance(Match match)
    {
        var minor = ParseMinorUnits(match.Groups["amount"].Value.TrimStart('+', '-'));
        var direction = match.Groups["direction"].Value;
        if (direction.Equals("Dr", StringComparison.OrdinalIgnoreCase) ||
            match.Groups["amount"].Value.StartsWith("-", StringComparison.Ordinal))
        {
            return -minor;
        }

        return minor;
    }

    private static string FormatMinorUnits(long value) =>
        (value / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string MetadataFingerprint(PdfDocumentEvidence evidence)
    {
        var fields = VisualLines(evidence)
            .Select(line => line.Text)
            .Where(line => !ShortDate.IsMatch(line))
            .Select(line => MonetaryToken.Replace(line, string.Empty))
            .Select(line => CollapseWhitespace(line.Normalize(NormalizationForm.FormKC)).ToLowerInvariant())
            .Where(line => line.Contains("account", StringComparison.Ordinal) ||
                line.Contains("card", StringComparison.Ordinal))
            .Where(line => line.Length > 0)
            .ToArray();
        if (fields.Length == 0)
        {
            throw new InvalidOperationException("INGEST-LAYOUT-A-METADATA");
        }

        return Sha256(string.Concat(AdapterVersion, "\n", string.Join("\n", fields)));
    }

    private static IEnumerable<VisualLine> VisualLines(PdfDocumentEvidence evidence, double baselineTolerance = 5d)
    {
        foreach (var page in evidence.Pages.OrderBy(page => page.PageNumber))
        {
            var rows = new List<List<PdfGlyphEvidence>>();
            foreach (var glyph in page.OrderedGlyphs
                .OrderByDescending(glyph => glyph.Bottom)
                .ThenBy(glyph => glyph.Left)
                .ThenBy(glyph => glyph.ContentOrder))
            {
                var row = rows.FirstOrDefault(candidate => Math.Abs(candidate[0].Bottom - glyph.Bottom) <= baselineTolerance);
                if (row is null)
                {
                    row = [];
                    rows.Add(row);
                }

                row.Add(glyph);
            }

            foreach (var row in rows.OrderByDescending(row => row[0].Bottom))
            {
                var ordered = row.OrderBy(glyph => glyph.Left).ThenBy(glyph => glyph.ContentOrder).ToArray();
                var widths = ordered.Select(glyph => Math.Max(0.1d, glyph.Right - glyph.Left)).Order().ToArray();
                var typicalWidth = widths[widths.Length / 2];
                var builder = new StringBuilder();
                var positions = new List<TextPosition>(ordered.Length);
                PdfGlyphEvidence? previous = null;
                foreach (var glyph in ordered)
                {
                    if (previous is not null &&
                        !char.IsWhiteSpace(glyph.Value, 0) &&
                        glyph.Left - previous.Right > Math.Max(1.5d, typicalWidth * 0.75d) &&
                        (builder.Length == 0 || !char.IsWhiteSpace(builder[^1])))
                    {
                        var gap = glyph.Left - previous.Right;
                        var spaces = Math.Max(1, (int)Math.Round(gap / typicalWidth, MidpointRounding.AwayFromZero));
                        builder.Append(' ', spaces);
                    }

                    var start = builder.Length;
                    builder.Append(glyph.Value);
                    positions.Add(new(start, builder.Length, glyph.Left));
                    previous = glyph;
                }

                yield return new(
                    page.PageNumber,
                    row.Average(glyph => glyph.Bottom),
                    builder.ToString(),
                    string.Concat(ordered.Select(glyph => glyph.Value)),
                    positions);
            }
        }
    }

    private static string ExtractContent(PdfDocumentEvidence evidence) =>
        string.Join("\n", evidence.Pages.OrderBy(page => page.PageNumber)
            .Select(page => string.Concat(page.OrderedGlyphs.OrderBy(glyph => glyph.ContentOrder).Select(glyph => glyph.Value))));

    private static string CollapseWhitespace(string value) => Whitespace.Replace(value.Trim(), " ");

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record RowAnchor(
        VisualLine Line,
        Match Balance,
        Match Date,
        string Description);

    private sealed record RowCandidate(
        int PageNumber,
        Match Balance,
        Match Date,
        string Description,
        DescriptionEvidenceKind DescriptionEvidenceKind,
        string RawEvidence);

    private sealed record RowExtraction(IReadOnlyList<RowCandidate> Rows, bool Complete);

    private sealed record StatementControls(long OpeningBalanceMinor, long ClosingBalanceMinor);

    private sealed record VisualLine(
        int PageNumber,
        double Bottom,
        string Text,
        string SourceText,
        IReadOnlyList<TextPosition> Positions);

    private sealed record TextPosition(int Start, int End, double Left);
}
