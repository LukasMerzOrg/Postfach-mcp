using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Postfach.Imap;

public sealed partial class MailService
{
    private static readonly Regex KeywordAtom = new(@"^\$?[A-Za-z0-9_.\-]{1,64}$", RegexOptions.Compiled);

    private static List<MessageRef> ParseIds(IReadOnlyCollection<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "ids must not be empty.");
        }

        if (ids.Count > MaxIdsPerCall)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, $"At most {MaxIdsPerCall} ids per call.", "Split the request into batches.");
        }

        var refs = ids.Select(MessageRef.Parse).Distinct().ToList();
        if (refs.Select(r => r.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "All ids in one call must belong to the same account.");
        }

        return refs;
    }

    private static (MessageFlags Flags, HashSet<string> Keywords) MapFlags(IEnumerable<string>? names)
    {
        var flags = MessageFlags.None;
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names ?? [])
        {
            var name = raw.Trim();
            switch (name.ToLowerInvariant())
            {
                case "read":
                case "seen":
                    flags |= MessageFlags.Seen;
                    break;
                case "flagged":
                case "starred":
                    flags |= MessageFlags.Flagged;
                    break;
                case "answered":
                    flags |= MessageFlags.Answered;
                    break;
                case "unread":
                    throw new PostfachException(ErrorCodes.InvalidArgument, "Use remove: [\"read\"] to mark as unread.");
                case "deleted":
                case "draft":
                    throw new PostfachException(ErrorCodes.InvalidArgument, $"'{name}' cannot be set directly.", "Use delete_messages or the draft tools.");
                default:
                    if (!KeywordAtom.IsMatch(name))
                    {
                        throw new PostfachException(ErrorCodes.InvalidArgument, $"'{name}' is not a valid keyword.", "Keywords are letters, digits, '_', '-', '.' and an optional leading '$'.");
                    }

                    keywords.Add(name);
                    break;
            }
        }

        return (flags, keywords);
    }

    public async Task<WriteResult> UpdateFlagsAsync(IReadOnlyCollection<string> ids, IReadOnlyCollection<string>? add, IReadOnlyCollection<string>? remove, CancellationToken ct)
    {
        var refs = ParseIds(ids);
        var (addFlags, addKeywords) = MapFlags(add);
        var (removeFlags, removeKeywords) = MapFlags(remove);
        if (addFlags == MessageFlags.None && addKeywords.Count == 0 && removeFlags == MessageFlags.None && removeKeywords.Count == 0)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "Nothing to change: pass add and/or remove.");
        }

        var account = _accounts.Get(refs[0].Account);
        return await WithClientAsync(account, retryable: true, async client =>
        {
            var updated = new List<string>();
            var conflicts = new List<string>();
            foreach (var group in refs.GroupBy(r => r.Folder))
            {
                var entry = await ResolveFolderAsync(account, client, group.Key, FolderRole.Inbox, ct);
                var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadWrite, ct);
                foreach (var r in group)
                {
                    EnsureValidity(folder, r);
                }

                var uids = group.Select(r => new UniqueId(r.UidValidity, r.Uid)).ToList();
                var failed = new HashSet<UniqueId>();
                if (addFlags != MessageFlags.None || addKeywords.Count > 0)
                {
                    failed.UnionWith(await StoreAsync(folder, uids, StoreAction.Add, addFlags, addKeywords, ct));
                }

                if (removeFlags != MessageFlags.None || removeKeywords.Count > 0)
                {
                    failed.UnionWith(await StoreAsync(folder, uids, StoreAction.Remove, removeFlags, removeKeywords, ct));
                }

                foreach (var r in group)
                {
                    (failed.Contains(new UniqueId(r.UidValidity, r.Uid)) ? conflicts : updated).Add(r.ToString());
                }
            }

            return new WriteResult { Updated = updated.Count, Ids = updated, Conflicts = conflicts.Count > 0 ? conflicts : null };
        }, ct);
    }

    /// <summary>
    /// STORE with UNCHANGEDSINCE when the folder supports CONDSTORE, so a concurrent change made in another client is
    /// not clobbered; conflicting UIDs are re-read and retried, then reported. Plain STORE otherwise (e.g. iCloud).
    /// </summary>
    private static async Task<IList<UniqueId>> StoreAsync(IMailFolder folder, IList<UniqueId> uids, StoreAction action,
        MessageFlags flags, HashSet<string> keywords, CancellationToken ct)
    {
        if (!folder.Supports(FolderFeature.ModSequences))
        {
            await folder.StoreAsync(uids, new StoreFlagsRequest(action, flags, keywords) { Silent = true }, ct);
            return [];
        }

        IList<UniqueId> pending = uids;
        for (var attempt = 0; attempt < 3 && pending.Count > 0; attempt++)
        {
            var current = await folder.FetchAsync(pending, MessageSummaryItems.UniqueId | MessageSummaryItems.ModSeq, ct);
            if (current.Count == 0)
            {
                return [];
            }

            var modSeq = current.Max(s => s.ModSeq ?? 0);
            var request = new StoreFlagsRequest(action, flags, keywords) { Silent = true, UnchangedSince = modSeq };
            pending = await folder.StoreAsync(current.Select(s => s.UniqueId).ToList(), request, ct);
        }

        return pending;
    }

    public async Task<WriteResult> MoveAsync(IReadOnlyCollection<string> ids, string to, CancellationToken ct)
    {
        var refs = ParseIds(ids);
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "'to' is required (a folder name or a role such as 'archive').");
        }

        var account = _accounts.Get(refs[0].Account);
        return await WithClientAsync(account, retryable: false, async client =>
        {
            var destination = await ResolveFolderAsync(account, client, to, FolderRole.Archive, ct);
            return await MoveCoreAsync(account, client, refs, destination, ct);
        }, ct);
    }

    public async Task<WriteResult> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var refs = ParseIds(ids);
        var account = _accounts.Get(refs[0].Account);
        return await WithClientAsync(account, retryable: false, async client =>
        {
            var map = await GetFolderMapAsync(account, client, ct);
            var trash = map.ByRole(FolderRole.Trash)
                ?? throw new PostfachException(ErrorCodes.Unsupported, $"Account '{account.Id}' has no Trash folder.", "Postfach never expunges outside Trash.");

            var inTrash = refs.Where(r => string.Equals(r.Folder, trash.FullName, StringComparison.Ordinal)).ToList();
            var elsewhere = refs.Except(inTrash).ToList();
            var result = elsewhere.Count > 0 ? await MoveCoreAsync(account, client, elsewhere, trash, ct) : new WriteResult();

            var purged = new List<string>();
            if (inTrash.Count > 0)
            {
                // Deleting from Trash is the only place Postfach expunges, and only the given UIDs.
                RequireUidPlus(client);
                var folder = await OpenAsync(client, trash.FullName, FolderAccess.ReadWrite, ct);
                foreach (var r in inTrash)
                {
                    EnsureValidity(folder, r);
                }

                var uids = inTrash.Select(r => new UniqueId(r.UidValidity, r.Uid)).ToList();
                await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                await folder.ExpungeAsync(uids, ct);
                purged.AddRange(inTrash.Select(r => r.ToString()));
            }

            return result with { Updated = result.Updated + purged.Count, Ids = result.Ids.Concat(purged).ToList() };
        }, ct);
    }

    private static void RequireUidPlus(ImapClient client)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            throw new PostfachException(ErrorCodes.Unsupported, "The server supports neither MOVE nor UIDPLUS.",
                "Postfach refuses a plain EXPUNGE because it could remove other messages marked \\Deleted.");
        }
    }

    private async Task<WriteResult> MoveCoreAsync(MailAccount account, ImapClient client, List<MessageRef> refs, FolderEntry destination, CancellationToken ct)
    {
        var target = string.Equals(destination.FullName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(destination.FullName, ct);
        var moved = new Dictionary<string, string?>();
        foreach (var group in refs.GroupBy(r => r.Folder))
        {
            var entry = await ResolveFolderAsync(account, client, group.Key, FolderRole.Inbox, ct);
            if (entry.FullName == destination.FullName)
            {
                foreach (var r in group)
                {
                    moved[r.ToString()] = r.ToString();
                }

                continue;
            }

            var folder = await OpenAsync(client, entry.FullName, FolderAccess.ReadWrite, ct);
            foreach (var r in group)
            {
                EnsureValidity(folder, r);
            }

            var uids = group.Select(r => new UniqueId(r.UidValidity, r.Uid)).ToList();
            UniqueIdMap map;
            if (client.Capabilities.HasFlag(ImapCapabilities.Move))
            {
                map = await folder.MoveToAsync(uids, target, ct);
            }
            else
            {
                // RFC 6851 fallback: COPY + STORE \Deleted + UID EXPUNGE. Never a plain EXPUNGE.
                RequireUidPlus(client);
                map = await folder.CopyToAsync(uids, target, ct);
                await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                await folder.ExpungeAsync(uids, ct);
            }

            var lookup = map.Source.Zip(map.Destination).ToDictionary(p => p.First.Id, p => p.Second);
            foreach (var r in group)
            {
                moved[r.ToString()] = lookup.TryGetValue(r.Uid, out var dest)
                    ? new MessageRef(account.Id, destination.FullName, dest.Validity, dest.Id).ToString()
                    : null;
            }
        }

        return new WriteResult { Updated = moved.Count, Ids = moved.Keys.ToList(), Moved = moved };
    }

    /// <summary>APPENDs a message with <c>\Draft \Seen</c> to Drafts and returns its id (via APPENDUID, else a Message-ID search).</summary>
    public Task<MessageRef> AppendDraftAsync(string accountId, MimeMessage message, CancellationToken ct) =>
        AppendAsync(accountId, FolderRole.Drafts, message, MessageFlags.Draft | MessageFlags.Seen, ct);

    public Task<MessageRef> AppendSentAsync(string accountId, MimeMessage message, CancellationToken ct) =>
        AppendAsync(accountId, FolderRole.Sent, message, MessageFlags.Seen, ct);

    private async Task<MessageRef> AppendAsync(string accountId, FolderRole role, MimeMessage message, MessageFlags flags, CancellationToken ct)
    {
        var account = _accounts.Get(accountId);
        return await WithClientAsync(account, retryable: false, async client =>
        {
            var entry = (await GetFolderMapAsync(account, client, ct)).ByRole(role)
                ?? await CreateRoleFolderAsync(account, client, role, ct);
            var folder = string.Equals(entry.FullName, "INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(entry.FullName, ct);
            var uid = await folder.AppendAsync(new AppendRequest(message, flags), ct);
            if (uid is { } appended && appended.Validity != 0)
            {
                return new MessageRef(account.Id, entry.FullName, appended.Validity, appended.Id);
            }

            // No APPENDUID (no UIDPLUS): find the message by its Message-ID.
            var opened = await OpenAsync(client, entry.FullName, FolderAccess.ReadOnly, ct);
            var hits = await opened.SearchAsync(SearchQuery.HeaderContains("Message-ID", message.MessageId ?? string.Empty), ct);
            var last = hits.Count > 0 ? hits.Max(h => h.Id) : 0;
            if (last == 0)
            {
                throw new PostfachException(ErrorCodes.BackendUnavailable, "The message was saved but its id could not be determined.", "Search the folder for it.");
            }

            return new MessageRef(account.Id, entry.FullName, opened.UidValidity, last);
        }, ct);
    }

    /// <summary>Creates a missing Drafts or Sent folder (marked special-use where CREATE-SPECIAL-USE is supported).</summary>
    private async Task<FolderEntry> CreateRoleFolderAsync(MailAccount account, ImapClient client, FolderRole role, CancellationToken ct)
    {
        var (name, use) = role switch
        {
            FolderRole.Drafts => ("Drafts", SpecialFolder.Drafts),
            FolderRole.Sent => ("Sent", SpecialFolder.Sent),
            _ => throw new PostfachException(ErrorCodes.NotFound, $"Account '{account.Id}' has no {role.ToWire()} folder."),
        };

        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        var parent = client.GetFolder(ns);
        _logger.LogInformation("Creating missing {Role} folder for account {Account}", role, account.Id);
        if (client.Capabilities.HasFlag(ImapCapabilities.CreateSpecialUse))
        {
            await parent.CreateAsync(name, [use], ct);
        }
        else
        {
            await parent.CreateAsync(name, true, ct);
        }

        var map = await GetFolderMapAsync(account, client, ct, refresh: true);
        return map.ByRole(role) ?? throw new PostfachException(ErrorCodes.BackendUnavailable, $"Could not create a {role.ToWire()} folder.");
    }

    /// <summary>Removes one message from Drafts (UID EXPUNGE with UIDPLUS; otherwise it stays flagged \Deleted).</summary>
    public async Task DeleteDraftAsync(MessageRef reference, CancellationToken ct)
    {
        var account = _accounts.Get(reference.Account);
        await WithClientAsync(account, retryable: false, async client =>
        {
            var folder = await OpenAsync(client, reference.Folder, FolderAccess.ReadWrite, ct);
            EnsureValidity(folder, reference);
            var uids = new[] { new UniqueId(reference.UidValidity, reference.Uid) };
            await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            {
                await folder.ExpungeAsync(uids, ct);
            }

            return true;
        }, ct);
    }

    /// <summary>Verifies that <paramref name="reference"/> points at a draft in this account's Drafts folder.</summary>
    public async Task EnsureDraftAsync(MessageRef reference, CancellationToken ct)
    {
        var account = _accounts.Get(reference.Account);
        await WithClientAsync(account, retryable: true, async client =>
        {
            var map = await GetFolderMapAsync(account, client, ct);
            var drafts = map.ByRole(FolderRole.Drafts);
            if (drafts is null || !string.Equals(drafts.FullName, reference.Folder, StringComparison.Ordinal))
            {
                throw new PostfachException(ErrorCodes.InvalidArgument, "This id is not a draft.", "Only messages in the Drafts folder can be updated or sent.");
            }

            return true;
        }, ct);
    }

    public async Task<bool> SupportsGmailExtensionsAsync(string accountId, CancellationToken ct)
    {
        var account = _accounts.Get(accountId);
        return await WithClientAsync(account, retryable: true, client => Task.FromResult(IsGmail(client)), ct);
    }
}
