using System.Globalization;

namespace Postfach.Imap;

/// <summary>
/// Stable message address <c>account:folder:uidvalidity:uid</c>. Including UIDVALIDITY means a reference that
/// outlived a mailbox rebuild fails with <c>stale_id</c> instead of pointing at a different message.
/// </summary>
public readonly record struct MessageRef(string Account, string Folder, uint UidValidity, uint Uid)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Account}:{Folder}:{UidValidity}:{Uid}");

    public static MessageRef Parse(string? value)
    {
        if (!TryParse(value, out var result))
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "Malformed message id.",
                "Ids look like 'account:folder:uidvalidity:uid'; use one returned by search_messages.");
        }

        return result;
    }

    public static bool TryParse(string? value, out MessageRef result)
    {
        result = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var first = value.IndexOf(':');
        var last = value.LastIndexOf(':');
        if (first <= 0 || last <= first)
        {
            return false;
        }

        var secondLast = value.LastIndexOf(':', last - 1);
        if (secondLast <= first)
        {
            return false;
        }

        var account = value[..first];
        var folder = value[(first + 1)..secondLast];
        if (folder.Length == 0
            || !uint.TryParse(value.AsSpan(secondLast + 1, last - secondLast - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var validity)
            || !uint.TryParse(value.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || uid == 0)
        {
            return false;
        }

        result = new MessageRef(account, folder, validity, uid);
        return true;
    }
}
