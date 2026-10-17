using MailKit;

namespace Postfach.Imap;

public enum FolderRole
{
    None,
    Inbox,
    Sent,
    Drafts,
    Archive,
    Trash,
    Junk,
    All,
    Flagged,
}

public static class FolderRoles
{
    private static readonly Dictionary<string, FolderRole> NameHeuristics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = FolderRole.Sent,
        ["Sent Items"] = FolderRole.Sent,
        ["Sent Messages"] = FolderRole.Sent,
        ["Gesendet"] = FolderRole.Sent,
        ["Gesendete Elemente"] = FolderRole.Sent,
        ["Gesendete Objekte"] = FolderRole.Sent,
        ["Gesendete Nachrichten"] = FolderRole.Sent,
        ["Drafts"] = FolderRole.Drafts,
        ["Draft"] = FolderRole.Drafts,
        ["Entwürfe"] = FolderRole.Drafts,
        ["Entwurf"] = FolderRole.Drafts,
        ["Trash"] = FolderRole.Trash,
        ["Deleted Items"] = FolderRole.Trash,
        ["Deleted Messages"] = FolderRole.Trash,
        ["Bin"] = FolderRole.Trash,
        ["Papierkorb"] = FolderRole.Trash,
        ["Gelöschte Elemente"] = FolderRole.Trash,
        ["Gelöschte Objekte"] = FolderRole.Trash,
        ["Junk"] = FolderRole.Junk,
        ["Junk E-Mail"] = FolderRole.Junk,
        ["Junk-E-Mail"] = FolderRole.Junk,
        ["Spam"] = FolderRole.Junk,
        ["Bulk Mail"] = FolderRole.Junk,
        ["Archive"] = FolderRole.Archive,
        ["Archiv"] = FolderRole.Archive,
        ["All Mail"] = FolderRole.All,
        ["Alle Nachrichten"] = FolderRole.All,
        // French, Italian, Spanish, Dutch, Portuguese
        ["Envoyés"] = FolderRole.Sent,
        ["Éléments envoyés"] = FolderRole.Sent,
        ["Brouillons"] = FolderRole.Drafts,
        ["Corbeille"] = FolderRole.Trash,
        ["Éléments supprimés"] = FolderRole.Trash,
        ["Indésirables"] = FolderRole.Junk,
        ["Courrier indésirable"] = FolderRole.Junk,
        ["Archives"] = FolderRole.Archive,
        ["Posta inviata"] = FolderRole.Sent,
        ["Inviata"] = FolderRole.Sent,
        ["Bozze"] = FolderRole.Drafts,
        ["Cestino"] = FolderRole.Trash,
        ["Posta indesiderata"] = FolderRole.Junk,
        ["Archivio"] = FolderRole.Archive,
        ["Enviados"] = FolderRole.Sent,
        ["Elementos enviados"] = FolderRole.Sent,
        ["Borradores"] = FolderRole.Drafts,
        ["Papelera"] = FolderRole.Trash,
        ["Correo no deseado"] = FolderRole.Junk,
        ["Archivo"] = FolderRole.Archive,
        ["Verzonden"] = FolderRole.Sent,
        ["Verzonden items"] = FolderRole.Sent,
        ["Concepten"] = FolderRole.Drafts,
        ["Prullenbak"] = FolderRole.Trash,
        ["Verwijderde items"] = FolderRole.Trash,
        ["Ongewenste e-mail"] = FolderRole.Junk,
        ["Archief"] = FolderRole.Archive,
        ["Itens enviados"] = FolderRole.Sent,
        ["Rascunhos"] = FolderRole.Drafts,
        ["Lixeira"] = FolderRole.Trash,
        ["Lixo"] = FolderRole.Trash,
        // Common oddities
        ["Sent Mail"] = FolderRole.Sent,
        ["Deleted"] = FolderRole.Trash,
        ["Bulk"] = FolderRole.Junk,
        ["Junk Mail"] = FolderRole.Junk,
        ["Junk Email"] = FolderRole.Junk,
    };

    /// <summary>RFC 6154 special-use attributes first, then German/English name heuristics.</summary>
    public static FolderRole Detect(FolderAttributes attributes, string fullName, string name)
    {
        if (string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase) || attributes.HasFlag(FolderAttributes.Inbox))
        {
            return FolderRole.Inbox;
        }

        if (attributes.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
        if (attributes.HasFlag(FolderAttributes.All)) return FolderRole.All;
        if (attributes.HasFlag(FolderAttributes.Flagged)) return FolderRole.Flagged;

        return FromName(name);
    }

    public static FolderRole FromName(string name) =>
        NameHeuristics.TryGetValue(name.Trim(), out var role) ? role : FolderRole.None;

    public static string? ToWire(this FolderRole role) => role == FolderRole.None ? null : role.ToString().ToLowerInvariant();

    public static bool TryParseWire(string value, out FolderRole role)
    {
        role = FolderRole.None;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out role)
            && role != FolderRole.None
            && !value.Trim().All(char.IsDigit);
    }
}

/// <summary>Folders of one account with their roles; cached briefly by <see cref="MailService"/>.</summary>
public sealed class FolderMap
{
    public FolderMap(IReadOnlyList<FolderEntry> folders, char separator)
    {
        Folders = folders;
        Separator = separator;
    }

    public IReadOnlyList<FolderEntry> Folders { get; }

    public char Separator { get; }

    public DateTimeOffset LoadedAt { get; } = DateTimeOffset.UtcNow;

    public FolderEntry? ByRole(FolderRole role)
    {
        // Prefer a special-use match over a name heuristic.
        return Folders.FirstOrDefault(f => f.Role == role && f.SpecialUse)
            ?? Folders.FirstOrDefault(f => f.Role == role);
    }

    public FolderEntry? ByName(string fullName) =>
        Folders.FirstOrDefault(f => string.Equals(f.FullName, fullName, StringComparison.Ordinal))
        ?? Folders.FirstOrDefault(f => string.Equals(f.FullName, fullName, StringComparison.OrdinalIgnoreCase));

    public FolderRole RoleOf(string fullName) => ByName(fullName)?.Role ?? FolderRoles.FromName(fullName);
}

public sealed record FolderEntry(string FullName, string Name, FolderRole Role, bool SpecialUse, bool Selectable, int? Unread, int? Total);
