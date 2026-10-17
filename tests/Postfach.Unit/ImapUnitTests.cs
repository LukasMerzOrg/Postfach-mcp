using MailKit;
using MailKit.Search;
using Postfach.Imap;

namespace Postfach.Unit;

public class MessageRefTests
{
    [Fact]
    public void Round_trips_folders_with_separators_and_colons()
    {
        var reference = new MessageRef("private", "[Gmail]/All Mail:x", 1712345678, 48211);
        var parsed = MessageRef.Parse(reference.ToString());
        Assert.Equal(reference, parsed);
        Assert.Equal("private:INBOX:1712345678:48211", new MessageRef("private", "INBOX", 1712345678, 48211).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("private:INBOX:1")]
    [InlineData("private::1:2")]
    [InlineData("private:INBOX:x:2")]
    [InlineData("private:INBOX:1:0")]
    [InlineData(":INBOX:1:2")]
    [InlineData("private:INBOX:-1:2")]
    public void Rejects_malformed(string? value)
    {
        Assert.False(MessageRef.TryParse(value, out _));
        var ex = Assert.Throws<PostfachException>(() => MessageRef.Parse(value));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }
}

public class SearchCompilerTests
{
    [Fact]
    public void Empty_criteria_is_all()
    {
        Assert.Same(SearchQuery.All, SearchCompiler.Compile(new SearchCriteria(), gmailRaw: false));
    }

    [Fact]
    public void Combines_terms_with_and()
    {
        var query = SearchCompiler.Compile(new SearchCriteria { From = "bob", Unread = true, Since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) }, false);
        Assert.Equal(SearchTerm.And, query.Term);
        var terms = Flatten(query).ToList();
        Assert.Contains(terms, t => t.Term == SearchTerm.FromContains);
        Assert.Contains(terms, t => t.Term == SearchTerm.NotSeen);
        Assert.Contains(terms, t => t.Term == SearchTerm.DeliveredAfter);
    }

    [Fact]
    public void Text_uses_x_gm_raw_on_gmail()
    {
        Assert.Equal(SearchTerm.GMailRaw, SearchCompiler.Compile(new SearchCriteria { Text = "has:attachment invoice" }, true).Term);
        Assert.Equal(SearchTerm.MessageContains, SearchCompiler.Compile(new SearchCriteria { Text = "invoice" }, false).Term);
    }

    [Fact]
    public void Attachment_filter_narrows_to_multipart_mixed()
    {
        var query = SearchCompiler.Compile(new SearchCriteria { HasAttachment = true }, false);
        Assert.Equal(SearchTerm.HeaderContains, query.Term);
        Assert.Same(SearchQuery.All, SearchCompiler.Compile(new SearchCriteria { HasAttachment = false }, false));
    }

    private static IEnumerable<SearchQuery> Flatten(SearchQuery query)
    {
        if (query is BinarySearchQuery binary)
        {
            return Flatten(binary.Left).Concat(Flatten(binary.Right));
        }

        return [query];
    }
}

public class FolderRoleTests
{
    [Theory]
    [InlineData("INBOX", "INBOX", FolderRole.Inbox)]
    [InlineData("Gesendet", "Gesendet", FolderRole.Sent)]
    [InlineData("INBOX.Entwürfe", "Entwürfe", FolderRole.Drafts)]
    [InlineData("Papierkorb", "Papierkorb", FolderRole.Trash)]
    [InlineData("Archiv", "Archiv", FolderRole.Archive)]
    [InlineData("Deleted Items", "Deleted Items", FolderRole.Trash)]
    [InlineData("Junk-E-Mail", "Junk-E-Mail", FolderRole.Junk)]
    [InlineData("Projects", "Projects", FolderRole.None)]
    public void Name_heuristics(string fullName, string name, FolderRole expected)
    {
        Assert.Equal(expected, FolderRoles.Detect(FolderAttributes.None, fullName, name));
    }

    [Fact]
    public void Special_use_wins_over_name()
    {
        Assert.Equal(FolderRole.Archive, FolderRoles.Detect(FolderAttributes.Archive, "Trash", "Trash"));
    }

    [Fact]
    public void Wire_roles()
    {
        Assert.True(FolderRoles.TryParseWire("archive", out var role));
        Assert.Equal(FolderRole.Archive, role);
        Assert.False(FolderRoles.TryParseWire("none", out _));
        Assert.False(FolderRoles.TryParseWire("3", out _));
        Assert.Equal("sent", FolderRole.Sent.ToWire());
    }
}
