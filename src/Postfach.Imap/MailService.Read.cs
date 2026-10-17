using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Postfach.Mime;

namespace Postfach.Imap;

public sealed partial class MailService
{
    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate
        | MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure | MessageSummaryItems.References
        | MessageSummaryItems.PreviewText;

    public async Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(string? accountId, CancellationToken ct)
    {
        var account = _accounts.Get(accountId);
        return await WithClientAsync(account, retryable: true, async client =>
        {
            var map = await GetFolderMapAsync(account, client, ct, refresh: true);
            return (IReadOnlyList<FolderInfo>)map.Folders
                .Where(f => f.Selectable)
                .OrderBy(f => f.Role == FolderRole.None ? 1 : 0)
                .ThenBy(f => f.Role)
                .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FolderInfo(f.FullName, f.Role.ToWire(), f.Unread, f.Total))
                .ToList();
        }, ct);
    }

    private sealed record Candidate(FolderEntry Folder, uint UidValidity, UniqueId Uid, DateTimeOffset Date);

    private sealed record Cursor(int Offset, string Fingerprint);

    public async Task<SearchResult> SearchAsync(string? accountId, string? folder, SearchCriteria criteria, int limit, string? cursor, CancellationToken ct)
    {
        var account = _accounts.Get(accountId);
        limit = Math.Clamp(limit <= 0 ? 25 : limit, 1, 100);
        var fingerprint = Fingerprint(account.Id, folder, criteria);
        var offset = DecodeCursor(cursor, fingerprint);

        return await WithClientAsync(account, retryable: true, async client =>
        {
            var map = await GetFolderMapAsync(account, client, ct);
            var skipped = new List<string>();
            List<FolderEntry> targets;
            if (string.Equals(folder?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                if (map.ByRole(FolderRole.All) is { Selectable: true } allMail)
                {
                    targets = [allMail];
                }
                else
                {
                    var ordered = map.Folders
                        .Where(f => f.Selectable && f.Role is not FolderRole.Trash and not FolderRole.Junk)
                        .OrderBy(f => f.Role switch { FolderRole.Inbox => 0, FolderRole.Sent => 1, FolderRole.Archive => 2, FolderRole.Drafts => 4, _ => 3 })
                        .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    targets = ordered.Take(MaxFoldersForAll).ToList();
                    skipped.AddRange(ordered.Skip(MaxFoldersForAll).Select(f => f.FullName));
                }
            }
            else
            {
                targets = [await ResolveFolderAsync(account, client, folder, FolderRole.Inbox, ct)];
            }

            var query = SearchCompiler.Compile(criteria, IsGmail(client));
            var needed = offset + limit;
            var candidates = new List<Candidate>();
            var total = 0;
            var capped = false;
            foreach (var target in targets)
            {
                var (found, count, wasCapped) = await CandidatesAsync(client, target, query, criteria.HasAttachment, needed, ct);
                candidates.AddRange(found);
                total += count;
                capped |= wasCapped;
            }

            var page = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => c.Uid.Id)
                .Skip(offset)
                .Take(limit)
                .ToList();

            var summaries = new Dictionary<(string, uint), MessageSummary>();
            foreach (var group in page.GroupBy(c => c.Folder.FullName))
            {
                var entry = group.First().Folder;
                var imapFolder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
                var items = SummaryItems | (IsGmail(client) ? MessageSummaryItems.GMailThreadId : 0);
                var fetched = await imapFolder.FetchAsync(group.Select(c => c.Uid).ToList(), items, ct);
                foreach (var s in fetched)
                {
                    summaries[(entry.FullName, s.UniqueId.Id)] = ToSummary(account.Id, entry, imapFolder.UidValidity, s);
                }
            }

            var messages = page
                .Select(c => summaries.GetValueOrDefault((c.Folder.FullName, c.Uid.Id)))
                .OfType<MessageSummary>()
                .ToList();

            return new SearchResult
            {
                Messages = messages,
                TotalMatches = total,
                Capped = capped,
                NextCursor = total > offset + limit ? EncodeCursor(new Cursor(offset + limit, fingerprint)) : null,
                SkippedFolders = skipped.Count > 0 ? skipped : null,
            };
        }, ct);
    }

    /// <summary>
    /// Newest-first candidate UIDs of one folder. Uses SORT when available; otherwise SEARCH, then INTERNALDATE for the
    /// hit set (capped at <see cref="MaxUidsPerSearch"/>) sorted client-side.
    /// </summary>
    private static async Task<(List<Candidate> Found, int Count, bool Capped)> CandidatesAsync(
        ImapClient client, FolderEntry entry, SearchQuery query, bool? hasAttachment, int needed, CancellationToken ct)
    {
        var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
        if (folder.Count == 0)
        {
            return ([], 0, false);
        }

        IList<UniqueId> uids;
        var sorted = false;
        if (client.Capabilities.HasFlag(ImapCapabilities.Sort) && hasAttachment is null)
        {
            uids = await folder.SortAsync(query, [OrderBy.ReverseArrival], ct);
            sorted = true;
        }
        else
        {
            uids = await folder.SearchAsync(query, ct);
        }

        var capped = uids.Count > MaxUidsPerSearch;
        var total = uids.Count;
        var pool = sorted
            ? uids.Take(Math.Min(needed, MaxUidsPerSearch)).ToList()
            : uids.OrderByDescending(u => u.Id).Take(MaxUidsPerSearch).ToList();
        if (pool.Count == 0)
        {
            return ([], 0, false);
        }

        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate
            | (hasAttachment is null ? 0 : MessageSummaryItems.BodyStructure);
        var fetched = await folder.FetchAsync(pool, items, ct);
        IEnumerable<IMessageSummary> filtered = fetched;
        if (hasAttachment is { } wanted)
        {
            filtered = fetched.Where(s => AttachmentParts(s).Any() == wanted);
            total = capped ? total : filtered.Count();
        }

        var candidates = filtered
            .Select(s => new Candidate(entry, folder.UidValidity, s.UniqueId, s.InternalDate ?? DateTimeOffset.MinValue))
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.Uid.Id)
            .Take(needed)
            .ToList();
        return (candidates, total, capped);
    }

    private static string Fingerprint(string account, string? folder, SearchCriteria criteria)
    {
        var json = JsonSerializer.Serialize(new { account, folder, criteria });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private static string EncodeCursor(Cursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int DecodeCursor(string? cursor, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            var decoded = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(b64));
            if (decoded is null || decoded.Fingerprint != fingerprint || decoded.Offset < 0)
            {
                throw new FormatException();
            }

            return decoded.Offset;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "The cursor does not belong to this search.",
                "Pass next_cursor together with exactly the same search inputs.");
        }
    }

    public async Task<MessageDetail> GetMessageAsync(string id, BodyFormat format, int maxChars, CancellationToken ct)
    {
        var reference = MessageRef.Parse(id);
        var account = _accounts.Get(reference.Account);
        maxChars = Math.Clamp(maxChars <= 0 ? 8000 : maxChars, 200, 100_000);

        return await WithClientAsync(account, retryable: true, async client =>
        {
            var entry = await ResolveFolderAsync(account, client, reference.Folder, FolderRole.Inbox, ct);
            var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
            EnsureValidity(folder, reference);
            var summary = await FetchOneAsync(client, folder, reference, ct);
            var headers = await folder.GetHeadersAsync(summary.UniqueId, ct);

            ExtractedBody body;
            if (format == BodyFormat.RawHeaders)
            {
                using var writer = new StringWriter();
                foreach (var h in headers)
                {
                    writer.Write($"{h.Field}: {h.Value}\n");
                }

                body = new ExtractedBody(writer.ToString().TrimEnd(), "headers", 0);
            }
            else
            {
                body = await ReadBodyAsync(folder, summary, format, ct);
            }

            var text = TextTrimmer.Truncate(body.Text, maxChars, out var truncated);
            var auth = MessageAnalysis.ParseAuthenticationResults(headers);
            var from = summary.Envelope?.From.Mailboxes.FirstOrDefault();
            return new MessageDetail
            {
                Id = reference.ToString(),
                ThreadId = ThreadIdFor(summary),
                Date = summary.Envelope?.Date ?? summary.InternalDate,
                From = ToAddress(from),
                To = ToAddresses(summary.Envelope?.To),
                Cc = ToAddresses(summary.Envelope?.Cc),
                ReplyTo = ToAddresses(summary.Envelope?.ReplyTo),
                Subject = summary.Envelope?.Subject,
                MessageIdHeader = summary.Envelope?.MessageId,
                Flags = WireFlags(summary.Flags, summary.Keywords),
                FolderRole = entry.Role.ToWire(),
                ListUnsubscribe = headers[HeaderId.ListUnsubscribe],
                Auth = new AuthResult(auth.Spf, auth.Dkim, auth.Dmarc),
                SenderMismatch = MessageAnalysis.SenderMismatch(from),
                Attachments = AttachmentParts(summary)
                    .Select(p => new AttachmentInfo(p.PartSpecifier, p.FileName, p.ContentType.MimeType.ToLowerInvariant(), p.Octets))
                    .ToList(),
                UntrustedContent = new UntrustedContent
                {
                    Body = text,
                    Truncated = truncated,
                    HiddenTextRemoved = body.HiddenTextRemoved,
                },
            };
        }, ct);
    }

    private static async Task<IMessageSummary> FetchOneAsync(ImapClient client, IMailFolder folder, MessageRef reference, CancellationToken ct)
    {
        var items = SummaryItems & ~MessageSummaryItems.PreviewText;
        if (IsGmail(client))
        {
            items |= MessageSummaryItems.GMailThreadId;
        }

        var fetched = await folder.FetchAsync([new UniqueId(reference.UidValidity, reference.Uid)], items, ct);
        return fetched.FirstOrDefault()
            ?? throw new PostfachException(ErrorCodes.NotFound, "Message not found.", "It may have been moved or deleted; search again.");
    }

    /// <summary>
    /// Fetches one MIME part (BODY.PEEK[part.MIME] + BODY.PEEK[part]). Servers that mishandle part fetches get a
    /// fallback: the whole message is downloaded (still with PEEK) and the part is located by its specifier.
    /// </summary>
    private static async Task<MimeEntity?> GetPartAsync(IMailFolder folder, IMessageSummary summary, BodyPart part, CancellationToken ct)
    {
        try
        {
            var entity = await folder.GetBodyPartAsync(summary.UniqueId, part, ct);
            if (entity is not null && entity.ContentType.IsMimeType(part.ContentType.MediaType, part.ContentType.MediaSubtype))
            {
                return entity;
            }
        }
        catch (FormatException)
        {
        }

        // The server returned a different section (seen with nested multiparts) or unparsable part headers.
        var message = await folder.GetMessageAsync(summary.UniqueId, ct);
        return FindPart(message, part.PartSpecifier);
    }

    internal static MimeEntity? FindPart(MimeMessage message, string specifier)
    {
        if (message.Body is not Multipart && specifier is "1" or "")
        {
            return message.Body;
        }

        using var iterator = new MimeIterator(message);
        while (iterator.MoveNext())
        {
            if (iterator.PathSpecifier == specifier)
            {
                return iterator.Current;
            }
        }

        return null;
    }

    /// <summary>Downloads only the text/plain and text/html parts (BODY.PEEK[part]) instead of the whole message.</summary>
    private static async Task<ExtractedBody> ReadBodyAsync(IMailFolder folder, IMessageSummary summary, BodyFormat format, CancellationToken ct)
    {
        string? plain = null, html = null;
        if (format == BodyFormat.Text && summary.TextBody is { } textPart)
        {
            plain = (await GetPartAsync(folder, summary, textPart, ct) as TextPart)?.Text;
        }

        if (string.IsNullOrWhiteSpace(plain) && summary.HtmlBody is { } htmlPart)
        {
            html = (await GetPartAsync(folder, summary, htmlPart, ct) as TextPart)?.Text;
        }

        if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(html) && summary.TextBody is { } fallback)
        {
            plain = (await GetPartAsync(folder, summary, fallback, ct) as TextPart)?.Text;
        }

        if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(html))
        {
            // No usable BODYSTRUCTURE or an unusual layout; parse the whole message locally.
            var message = await folder.GetMessageAsync(summary.UniqueId, ct);
            return BodyExtractor.Extract(message, format);
        }

        return BodyExtractor.FromParts(plain, html, format);
    }

    public async Task<ThreadResult> GetThreadAsync(string id, CancellationToken ct)
    {
        var reference = MessageRef.Parse(id);
        var account = _accounts.Get(reference.Account);
        const int maxMessages = 50;
        const int maxCharsPerMessage = 1500;

        return await WithClientAsync(account, retryable: true, async client =>
        {
            var map = await GetFolderMapAsync(account, client, ct);
            var origin = await ResolveFolderAsync(account, client, reference.Folder, FolderRole.Inbox, ct);
            var originFolder = await OpenAsync(client, origin.FullName, FolderAccess.ReadOnly, ct);
            EnsureValidity(originFolder, reference);
            var start = await FetchOneAsync(client, originFolder, reference, ct);

            var members = new List<(FolderEntry Folder, uint Validity, UniqueId Uid)>();
            string method;
            if (start.GMailThreadId is { } thrid && map.ByRole(FolderRole.All) is { } allMail)
            {
                method = "gmail_thrid";
                var folder = await OpenAsync(client, allMail.FullName, FolderAccess.ReadOnly, ct);
                foreach (var uid in await folder.SearchAsync(SearchQuery.GMailThreadId(thrid), ct))
                {
                    members.Add((allMail, folder.UidValidity, uid));
                }
            }
            else
            {
                var ids = new List<string>();
                if (start.References is { } refs) ids.AddRange(refs);
                if (start.Envelope?.InReplyTo is { } irt) ids.Add(irt);
                if (start.Envelope?.MessageId is { } mid) ids.Add(mid);
                ids = ids.Select(i => i.Trim('<', '>')).Where(i => i.Length > 0).Distinct().ToList();
                // Root + the most recent ancestors is enough to find every reply.
                if (ids.Count > 12)
                {
                    ids = ids.Take(2).Concat(ids.TakeLast(10)).Distinct().ToList();
                }

                method = "references";
                var folders = new[] { origin, map.ByRole(FolderRole.Inbox), map.ByRole(FolderRole.Sent), map.ByRole(FolderRole.Archive) }
                    .OfType<FolderEntry>()
                    .Where(f => f.Selectable)
                    .DistinctBy(f => f.FullName)
                    .ToList();

                foreach (var entry in folders)
                {
                    var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
                    var found = new HashSet<UniqueId>();
                    if (entry.FullName == origin.FullName && client.Capabilities.HasFlag(ImapCapabilities.Thread) && client.ThreadingAlgorithms.Contains(ThreadingAlgorithm.References))
                    {
                        // RFC 5256 THREAD=REFERENCES over a time window around the message.
                        var anchor = start.InternalDate ?? DateTimeOffset.UtcNow;
                        var window = SearchQuery.DeliveredAfter(anchor.AddDays(-180).Date);
                        var threads = await folder.ThreadAsync(ThreadingAlgorithm.References, window, ct);
                        var tree = threads.FirstOrDefault(t => Contains(t, start.UniqueId));
                        if (tree is not null)
                        {
                            method = "thread_references";
                            Collect(tree, found);
                        }
                    }

                    if (ids.Count > 0)
                    {
                        SearchQuery? query = null;
                        foreach (var messageId in ids)
                        {
                            var term = SearchQuery.HeaderContains("Message-ID", messageId)
                                .Or(SearchQuery.HeaderContains("References", messageId))
                                .Or(SearchQuery.HeaderContains("In-Reply-To", messageId));
                            query = query is null ? term : query.Or(term);
                        }

                        foreach (var uid in await folder.SearchAsync(query!, ct))
                        {
                            found.Add(uid);
                        }
                    }

                    if (entry.FullName == origin.FullName)
                    {
                        found.Add(start.UniqueId);
                    }

                    members.AddRange(found.Select(u => (entry, folder.UidValidity, u)));
                }
            }

            var messages = new List<(ThreadMessage Message, string? MessageId, DateTimeOffset Date)>();
            foreach (var group in members.GroupBy(m => m.Folder.FullName))
            {
                var entry = group.First().Folder;
                var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
                var uids = group.Select(m => m.Uid).Distinct().OrderByDescending(u => u.Id).Take(maxMessages).ToList();
                var fetched = await folder.FetchAsync(uids, SummaryItems & ~MessageSummaryItems.PreviewText, ct);
                foreach (var s in fetched)
                {
                    var body = await ReadBodyAsync(folder, s, BodyFormat.Text, ct);
                    var trimmed = TextTrimmer.Truncate(TextTrimmer.TrimHistoryAndSignature(body.Text), maxCharsPerMessage, out var truncated);
                    var date = s.Envelope?.Date ?? s.InternalDate ?? DateTimeOffset.MinValue;
                    messages.Add((new ThreadMessage
                    {
                        Id = new MessageRef(account.Id, entry.FullName, folder.UidValidity, s.UniqueId.Id).ToString(),
                        Date = date,
                        From = ToAddress(s.Envelope?.From.Mailboxes.FirstOrDefault()),
                        To = s.Envelope?.To.Mailboxes.Select(m => m.Address).ToList() ?? [],
                        Subject = s.Envelope?.Subject,
                        Flags = WireFlags(s.Flags, s.Keywords),
                        FolderRole = entry.Role.ToWire(),
                        Attachments = AttachmentParts(s).Count(),
                        UntrustedContent = new UntrustedContent { Body = trimmed, Truncated = truncated, HiddenTextRemoved = body.HiddenTextRemoved },
                    }, s.Envelope?.MessageId, date));
                }
            }

            // The same message can sit in several folders (e.g. Inbox and Archive copies); keep one.
            var ordered = messages
                .GroupBy(m => m.MessageId ?? m.Message.Id)
                .Select(g => g.OrderBy(m => m.Message.FolderRole == "inbox" ? 0 : 1).First())
                .OrderBy(m => m.Date)
                .TakeLast(maxMessages)
                .Select(m => m.Message)
                .ToList();
            return new ThreadResult(ThreadIdFor(start), ordered, method);
        }, ct);
    }

    private static bool Contains(MessageThread thread, UniqueId uid) =>
        thread.UniqueId == uid || thread.Children.Any(c => Contains(c, uid));

    private static void Collect(MessageThread thread, HashSet<UniqueId> into)
    {
        if (thread.UniqueId is { } uid)
        {
            into.Add(uid);
        }

        foreach (var child in thread.Children)
        {
            Collect(child, into);
        }
    }

    public async Task<AttachmentResult> GetAttachmentAsync(string id, string part, int maxChars, CancellationToken ct)
    {
        var reference = MessageRef.Parse(id);
        var account = _accounts.Get(reference.Account);
        if (string.IsNullOrWhiteSpace(part))
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "part is required.", "Use a part from get_message's attachments list.");
        }

        maxChars = Math.Clamp(maxChars <= 0 ? 20_000 : maxChars, 200, 200_000);
        return await WithClientAsync(account, retryable: true, async client =>
        {
            var entry = await ResolveFolderAsync(account, client, reference.Folder, FolderRole.Inbox, ct);
            var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
            EnsureValidity(folder, reference);
            var summary = await FetchOneAsync(client, folder, reference, ct);
            var bodyPart = summary.BodyParts.FirstOrDefault(p => p.PartSpecifier == part.Trim())
                ?? throw new PostfachException(ErrorCodes.NotFound, $"Part '{part}' not found in this message.", "Use a part from get_message's attachments list.");

            var mime = bodyPart.ContentType.MimeType.ToLowerInvariant();
            var result = new AttachmentResult
            {
                Id = reference.ToString(),
                Part = bodyPart.PartSpecifier,
                Filename = bodyPart.FileName,
                Mime = mime,
                Size = bodyPart.Octets,
            };

            if (AttachmentText.IsBlocked(bodyPart.FileName, mime))
            {
                return result with { Reason = "blocked_type" };
            }

            // Octets is the encoded size; base64 inflates by ~4/3.
            if (bodyPart.Octets * 3 / 4 > AttachmentText.MaxExtractBytes)
            {
                return result with { Reason = "too_large" };
            }

            var entity = await GetPartAsync(folder, summary, bodyPart, ct)
                ?? throw new PostfachException(ErrorCodes.NotFound, $"Part '{part}' not found in this message.");
            var extraction = AttachmentText.Extract(entity, bodyPart.Octets * 3 / 4, maxChars);
            return extraction.Text is null
                ? result with { Reason = extraction.Reason }
                : result with { UntrustedContent = new UntrustedContent { Text = extraction.Text, Truncated = extraction.Truncated } };
        }, ct);
    }

    /// <summary>Downloads a full message (used for drafts, replies and forwards).</summary>
    public async Task<MimeMessage> GetMimeMessageAsync(MessageRef reference, CancellationToken ct)
    {
        var account = _accounts.Get(reference.Account);
        return await WithClientAsync(account, retryable: true, async client =>
        {
            var folder = await OpenAsync(client, reference.Folder, FolderAccess.ReadOnly, ct);
            EnsureValidity(folder, reference);
            return await folder.GetMessageAsync(new UniqueId(reference.UidValidity, reference.Uid), ct);
        }, ct);
    }
}
