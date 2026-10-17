using System.Collections.Concurrent;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using MimeKit;
using Postfach.Mime;

namespace Postfach.Imap;

/// <summary>
/// All IMAP access. Read operations use EXAMINE and BODY.PEEK so reading never changes flags; every command is
/// UID-based. Capabilities are probed per connection and pick the code path per feature, never per provider.
/// </summary>
public sealed partial class MailService : IAsyncDisposable
{
    public const int MaxIdsPerCall = 100;
    public const int MaxUidsPerSearch = 500;
    public const int MaxFoldersForAll = 20;

    private static readonly TimeSpan FolderCacheTtl = TimeSpan.FromMinutes(10);

    private readonly IAccountRegistry _accounts;
    private readonly ImapConnector _connector;
    private readonly PoolOptions _poolOptions;
    private readonly ILogger<MailService> _logger;
    private readonly ConcurrentDictionary<string, ImapConnectionPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FolderMap> _folderMaps = new(StringComparer.OrdinalIgnoreCase);

    public MailService(IAccountRegistry accounts, ImapConnector connector, ILogger<MailService> logger, PoolOptions? poolOptions = null)
    {
        _accounts = accounts;
        _connector = connector;
        _logger = logger;
        _poolOptions = poolOptions ?? new PoolOptions();
    }

    public IAccountRegistry Accounts => _accounts;

    public ImapCapabilities? KnownCapabilities(string accountId) =>
        _pools.TryGetValue(accountId, out var pool) ? pool.LastCapabilities : null;

    private ImapConnectionPool Pool(MailAccount account) =>
        _pools.GetOrAdd(account.Id, _ => new ImapConnectionPool(account, _connector, _poolOptions, _logger));

    /// <summary>Runs <paramref name="operation"/> on a pooled connection with error mapping and one retry when safe.</summary>
    internal async Task<T> WithClientAsync<T>(MailAccount account, bool retryable, Func<ImapClient, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lease = await Pool(account).AcquireAsync(ct);
            try
            {
                return await operation(lease.Client);
            }
            catch (PostfachException)
            {
                throw;
            }
            catch (FolderNotFoundException ex)
            {
                _folderMaps.TryRemove(account.Id, out _);
                throw new PostfachException(ErrorCodes.NotFound, "Folder not found.", "Call list_folders for valid folder names.", ex);
            }
            catch (MessageNotFoundException ex)
            {
                throw new PostfachException(ErrorCodes.NotFound, "Message not found.", "It may have been moved or deleted; search again.", ex);
            }
            catch (ImapCommandException ex) when (IsThrottled(ex) && attempt == 1)
            {
                _logger.LogInformation("IMAP server for {Account} is throttling; backing off", account.Id);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (ImapCommandException ex)
            {
                _logger.LogWarning("IMAP command failed for {Account}: {Status}", account.Id, ex.Response);
                throw new PostfachException(ErrorCodes.BackendUnavailable, $"The IMAP server refused the command ({ex.Response}).", null, ex);
            }
            catch (Exception ex) when (ex is IOException or ImapProtocolException or SocketException or ServiceNotConnectedException or ServiceNotAuthenticatedException)
            {
                lease.MarkBroken();
                if (!(retryable && attempt == 1))
                {
                    throw new PostfachException(ErrorCodes.BackendUnavailable, "Lost the connection to the IMAP server.", "Try again.", ex);
                }

                _logger.LogInformation("IMAP connection for {Account} dropped ({Error}); retrying", account.Id, ex.GetType().Name);
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static bool IsThrottled(ImapCommandException ex)
    {
        var text = ex.ResponseText ?? string.Empty;
        return text.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || text.Contains("try again", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Server Unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[UNAVAILABLE]", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[LIMIT]", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<FolderMap> GetFolderMapAsync(MailAccount account, ImapClient client, CancellationToken ct, bool refresh = false)
    {
        if (!refresh && _folderMaps.TryGetValue(account.Id, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < FolderCacheTtl)
        {
            return cached;
        }

        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        IList<IMailFolder> folders;
        try
        {
            folders = await client.GetFoldersAsync(ns, StatusItems.Count | StatusItems.Unread, false, ct);
        }
        catch (ImapCommandException)
        {
            // Some servers fail STATUS on odd folders; fall back to a plain LIST.
            folders = await client.GetFoldersAsync(ns, StatusItems.None, false, ct);
        }

        var special = new Dictionary<string, FolderRole>(StringComparer.Ordinal);
        if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || client.Capabilities.HasFlag(ImapCapabilities.XList))
        {
            foreach (var (kind, role) in new[]
                     {
                         (SpecialFolder.Sent, FolderRole.Sent), (SpecialFolder.Drafts, FolderRole.Drafts),
                         (SpecialFolder.Trash, FolderRole.Trash), (SpecialFolder.Junk, FolderRole.Junk),
                         (SpecialFolder.Archive, FolderRole.Archive), (SpecialFolder.All, FolderRole.All),
                         (SpecialFolder.Flagged, FolderRole.Flagged),
                     })
            {
                try
                {
                    if (client.GetFolder(kind) is { } f)
                    {
                        special.TryAdd(f.FullName, role);
                    }
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        var entries = new List<FolderEntry>();
        var all = folders.ToList();
        if (!all.Any(f => string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase)))
        {
            all.Insert(0, client.Inbox);
        }

        foreach (var folder in all)
        {
            var selectable = !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent);
            var isSpecial = special.TryGetValue(folder.FullName, out var role);
            if (!isSpecial)
            {
                role = FolderRoles.Detect(folder.Attributes, folder.FullName, folder.Name);
                isSpecial = role != FolderRole.None && role != FolderRole.Inbox && FolderRoles.FromName(folder.Name) != role;
            }

            entries.Add(new FolderEntry(folder.FullName, folder.Name, role, isSpecial, selectable,
                folder.Unread >= 0 && selectable ? folder.Unread : null, folder.Count >= 0 && selectable ? folder.Count : null));
        }

        var map = new FolderMap(entries, ns.DirectorySeparator);
        _folderMaps[account.Id] = map;
        return map;
    }

    internal async Task<FolderEntry> ResolveFolderAsync(MailAccount account, ImapClient client, string? folder, FolderRole fallback, CancellationToken ct)
    {
        var map = await GetFolderMapAsync(account, client, ct);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return map.ByRole(fallback)
                ?? throw new PostfachException(ErrorCodes.NotFound, $"Account '{account.Id}' has no {fallback.ToWire()} folder.");
        }

        if (map.ByName(folder) is { } byName)
        {
            return byName;
        }

        if (FolderRoles.TryParseWire(folder, out var role) && map.ByRole(role) is { } byRole)
        {
            return byRole;
        }

        // The folder may be new since the cache was filled.
        map = await GetFolderMapAsync(account, client, ct, refresh: true);
        return map.ByName(folder)
            ?? throw new PostfachException(ErrorCodes.NotFound, $"Folder '{folder}' not found.", "Call list_folders for valid names, or use a role such as 'archive'.");
    }

    internal static async Task<IMailFolder> OpenAsync(ImapClient client, string fullName, FolderAccess access, CancellationToken ct)
    {
        var folder = string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(fullName, ct);
        if (!folder.IsOpen || folder.Access != access)
        {
            // FolderAccess.ReadOnly issues EXAMINE: reading never clears \Recent or sets \Seen.
            await folder.OpenAsync(access, ct);
        }

        return folder;
    }

    internal static void EnsureValidity(IMailFolder folder, MessageRef reference)
    {
        if (folder.UidValidity != reference.UidValidity)
        {
            throw new PostfachException(ErrorCodes.StaleId, "This message id is stale: the folder was rebuilt on the server.",
                "Search again to get current ids.");
        }
    }

    internal static bool IsGmail(ImapClient client) => client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);

    internal static IReadOnlyList<string> WireFlags(MessageFlags? flags, IReadOnlySet<string>? keywords)
    {
        var result = new List<string>();
        var f = flags ?? MessageFlags.None;
        if (!f.HasFlag(MessageFlags.Seen)) result.Add("unread");
        if (f.HasFlag(MessageFlags.Flagged)) result.Add("flagged");
        if (f.HasFlag(MessageFlags.Answered)) result.Add("answered");
        if (f.HasFlag(MessageFlags.Draft)) result.Add("draft");
        if (f.HasFlag(MessageFlags.Deleted)) result.Add("deleted");
        if (keywords is not null)
        {
            result.AddRange(keywords.Where(k => !k.StartsWith('\\')).Order(StringComparer.Ordinal));
        }

        return result;
    }

    internal static EmailAddress? ToAddress(InternetAddress? address) => address switch
    {
        MailboxAddress m => new EmailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address),
        _ => null,
    };

    internal static IReadOnlyList<EmailAddress> ToAddresses(InternetAddressList? list) =>
        list?.Mailboxes.Select(m => new EmailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToList() ?? [];

    internal static IEnumerable<BodyPartBasic> AttachmentParts(IMessageSummary summary)
    {
        if (summary.Body is null)
        {
            return [];
        }

        return summary.BodyParts.Where(p =>
            p.IsAttachment
            || (!string.IsNullOrEmpty(p.FileName) && p.ContentId is null && !p.ContentType.IsMimeType("text", "plain") && !p.ContentType.IsMimeType("text", "html")));
    }

    internal static string ThreadIdFor(IMessageSummary summary)
    {
        if (summary.GMailThreadId is { } gm)
        {
            return "g_" + gm.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        var envelope = summary.Envelope;
        return MessageAnalysis.ThreadKey(envelope?.MessageId, summary.References, envelope?.InReplyTo);
    }

    internal static MessageSummary ToSummary(string accountId, FolderEntry folder, uint uidValidity, IMessageSummary s) => new()
    {
        Id = new MessageRef(accountId, folder.FullName, uidValidity, s.UniqueId.Id).ToString(),
        ThreadId = ThreadIdFor(s),
        Date = s.Envelope?.Date ?? s.InternalDate,
        From = ToAddress(s.Envelope?.From.Mailboxes.FirstOrDefault()),
        To = s.Envelope?.To.Mailboxes.Select(m => m.Address).ToList() ?? [],
        Subject = s.Envelope?.Subject,
        Snippet = TextTrimmer.Snippet(s.PreviewText),
        Flags = WireFlags(s.Flags, s.Keywords),
        Attachments = AttachmentParts(s).Count(),
        FolderRole = folder.Role.ToWire(),
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools.Values)
        {
            await pool.DisposeAsync();
        }

        _pools.Clear();
    }
}
