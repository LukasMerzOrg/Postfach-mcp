using MailKit.Search;

namespace Postfach.Imap;

public sealed record SearchCriteria
{
    public string? From { get; init; }

    public string? To { get; init; }

    public string? Subject { get; init; }

    public string? Text { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Before { get; init; }

    public bool? Unread { get; init; }

    public bool? Flagged { get; init; }

    public bool? HasAttachment { get; init; }
}

/// <summary>Compiles structured search inputs to IMAP SEARCH keys.</summary>
public static class SearchCompiler
{
    /// <param name="gmailRaw">When the server advertises X-GM-EXT-1, <c>text</c> is passed through as X-GM-RAW (Gmail search syntax).</param>
    public static SearchQuery Compile(SearchCriteria criteria, bool gmailRaw)
    {
        var terms = new List<SearchQuery>();
        if (!string.IsNullOrWhiteSpace(criteria.From)) terms.Add(SearchQuery.FromContains(criteria.From.Trim()));
        if (!string.IsNullOrWhiteSpace(criteria.To)) terms.Add(SearchQuery.ToContains(criteria.To.Trim()));
        if (!string.IsNullOrWhiteSpace(criteria.Subject)) terms.Add(SearchQuery.SubjectContains(criteria.Subject.Trim()));
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            terms.Add(gmailRaw ? SearchQuery.GMailRawSearch(criteria.Text.Trim()) : SearchQuery.MessageContains(criteria.Text.Trim()));
        }

        // IMAP SINCE/BEFORE compare dates only (no time), against the internal date.
        if (criteria.Since is { } since) terms.Add(SearchQuery.DeliveredAfter(since.Date));
        if (criteria.Before is { } before) terms.Add(SearchQuery.DeliveredBefore(before.Date));
        if (criteria.Unread is { } unread) terms.Add(unread ? SearchQuery.NotSeen : SearchQuery.Seen);
        if (criteria.Flagged is { } flagged) terms.Add(flagged ? SearchQuery.Flagged : SearchQuery.NotFlagged);

        // IMAP has no attachment key. Narrow to multipart/mixed server-side; the BODYSTRUCTURE check is exact.
        if (criteria.HasAttachment == true) terms.Add(SearchQuery.HeaderContains("Content-Type", "multipart/mixed"));

        if (terms.Count == 0)
        {
            return SearchQuery.All;
        }

        var query = terms[0];
        for (var i = 1; i < terms.Count; i++)
        {
            query = query.And(terms[i]);
        }

        return query;
    }
}
