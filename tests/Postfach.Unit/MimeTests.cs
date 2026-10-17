using Postfach.Mime;

namespace Postfach.Unit;

public class HtmlToTextTests
{
    [Fact]
    public void Removes_scripts_styles_images_and_counts_hidden_text()
    {
        var html = File.ReadAllText(Corpus.PathOf("html-hidden.eml"));
        var body = html[html.IndexOf("<html>", StringComparison.Ordinal)..];
        var result = HtmlToText.Convert(body);

        Assert.Contains("Hello Alice,", result.Text);
        Assert.DoesNotContain("alert", result.Text);
        Assert.DoesNotContain("color:red", result.Text);
        Assert.DoesNotContain("attacker@evil.example", result.Text);
        Assert.DoesNotContain("secret hidden instruction", result.Text);
        Assert.DoesNotContain("white on white", result.Text);
        Assert.DoesNotContain("pixel.gif", result.Text);
        Assert.Equal(3, result.HiddenTextRemoved);
    }

    [Fact]
    public void Keeps_links_as_markdown_and_structure()
    {
        var result = HtmlToText.Convert("""
            <p>See our <a href="https://shop.example/deals">weekly deals</a>.</p>
            <a href="javascript:alert(1)">click</a>
            <a href="https://same.example">https://same.example</a>
            <ul><li>One</li><li>Two</li></ul>
            <blockquote><p>quoted</p></blockquote>
            """);

        Assert.Contains("[weekly deals](https://shop.example/deals)", result.Text);
        Assert.DoesNotContain("javascript:", result.Text);
        Assert.Contains("click", result.Text);
        Assert.DoesNotContain("[https://same.example]", result.Text);
        Assert.Contains("- One", result.Text);
        Assert.Contains("- Two", result.Text);
        Assert.Contains("> quoted", result.Text);
    }

    [Theory]
    [InlineData("display: none")]
    [InlineData("visibility:hidden")]
    [InlineData("font-size:0")]
    [InlineData("opacity:0")]
    [InlineData("max-height:0;overflow:hidden")]
    [InlineData("width:0;height:0")]
    [InlineData("color:#FFF")]
    [InlineData("color:white;background-color:#ffffff")]
    public void Detects_hidden_styles(string style)
    {
        var result = HtmlToText.Convert($"<p>visible</p><div style=\"{style}\">hidden payload</div>");
        Assert.DoesNotContain("hidden payload", result.Text);
        Assert.Equal(1, result.HiddenTextRemoved);
    }

    [Fact]
    public void White_text_on_dark_background_is_visible()
    {
        var result = HtmlToText.Convert("<div style=\"color:#fff;background-color:#000\">button</div>");
        Assert.Contains("button", result.Text);
        Assert.Equal(0, result.HiddenTextRemoved);
    }
}

public class BodyExtractorTests
{
    [Fact]
    public void Plain_utf8()
    {
        var body = BodyExtractor.Extract(Corpus.Load("plain-utf8.eml"));
        Assert.Equal("text/plain", body.Source);
        Assert.Contains("Grüngutabfuhr", body.Text);
    }

    [Fact]
    public void Undeclared_latin1_is_decoded()
    {
        var body = BodyExtractor.Extract(Corpus.Load("latin1-undeclared.eml"));
        Assert.Contains("Grüezi", body.Text);
        Assert.Contains("fällig", body.Text);
    }

    [Fact]
    public void Html_only_message_is_sanitized()
    {
        var body = BodyExtractor.Extract(Corpus.Load("html-hidden.eml"));
        Assert.Equal("text/html", body.Source);
        Assert.Equal(3, body.HiddenTextRemoved);
        Assert.DoesNotContain("attacker", body.Text);
    }

    [Fact]
    public void Prefers_plain_and_html_stripped_uses_html()
    {
        var message = Corpus.Load("nested-multipart.eml");
        Assert.StartsWith("Plain version", BodyExtractor.Extract(message).Text);
        Assert.StartsWith("HTML version", BodyExtractor.Extract(message, BodyFormat.HtmlStripped).Text);
    }

    [Fact]
    public void Encoded_headers_are_decoded()
    {
        var message = Corpus.Load("encoded-headers.eml");
        Assert.Equal("Übersicht Rechnung ✓", message.Subject);
        Assert.Equal("Jürg Müller", message.From.Mailboxes.Single().Name);
        Assert.Equal("Übersicht 2026.csv", message.Attachments.Single().ContentDisposition!.FileName);
        Assert.Contains("Übersicht", BodyExtractor.Extract(message).Text);
    }

    [Fact]
    public void Signed_and_tnef_messages_have_text()
    {
        Assert.Contains("Kontoauszug", BodyExtractor.Extract(Corpus.Load("smime-signed.eml")).Text);
        Assert.Equal("See attached.", BodyExtractor.Extract(Corpus.Load("tnef.eml")).Text);
    }

    [Fact]
    public void Raw_headers()
    {
        var text = BodyExtractor.Extract(Corpus.Load("phishing.eml"), BodyFormat.RawHeaders).Text;
        Assert.Contains("Authentication-Results:", text);
        Assert.DoesNotContain("Verify your account", text);
    }

    [Fact]
    public void Quotes_collapse_after_three_lines()
    {
        var text = BodyExtractor.Extract(Corpus.Load("quoted-long.eml")).Text;
        Assert.Contains("> line 3", text);
        Assert.DoesNotContain("> line 4", text);
        Assert.Contains("[… 3 quoted lines collapsed]", text);
    }
}

public class TextTrimmerTests
{
    [Fact]
    public void Trims_signature_and_history()
    {
        var text = BodyExtractor.Extract(Corpus.Load("thread-2.eml")).Text;
        Assert.Equal("Super, dann um 10 Uhr am Bahnhof.", TextTrimmer.TrimHistoryAndSignature(text));
    }

    [Fact]
    public void Trims_german_attribution()
    {
        var text = BodyExtractor.Extract(Corpus.Load("thread-sent.eml")).Text;
        Assert.Equal("Ja, gerne! Wann?", TextTrimmer.TrimHistoryAndSignature(text));
    }

    [Fact]
    public void Trims_outlook_history()
    {
        var text = "Thanks!\n\n-----Original Message-----\nFrom: x\nold";
        Assert.Equal("Thanks!", TextTrimmer.TrimHistoryAndSignature(text));
    }

    [Fact]
    public void Snippet_is_single_line_and_bounded()
    {
        var snippet = TextTrimmer.Snippet("Line one\n\n> quoted\nline   two " + new string('x', 300));
        Assert.StartsWith("Line one line two", snippet);
        Assert.Equal(160, snippet.Length);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Truncate_marks_truncation()
    {
        var text = TextTrimmer.Truncate(new string('a', 50) + " " + new string('b', 100), 100, out var truncated);
        Assert.True(truncated);
        Assert.True(text.Length <= 102);
        Assert.Equal("short", TextTrimmer.Truncate("short", 100, out var t2));
        Assert.False(t2);
    }
}

public class MessageAnalysisTests
{
    [Fact]
    public void Uses_topmost_authentication_results_only()
    {
        var auth = MessageAnalysis.ParseAuthenticationResults(Corpus.Load("phishing.eml").Headers);
        Assert.Equal(new AuthVerdict("fail", "none", "fail"), auth);
    }

    [Fact]
    public void Detects_display_name_spoofing()
    {
        Assert.True(MessageAnalysis.SenderMismatch(Corpus.Load("phishing.eml").From.Mailboxes.Single()));
        Assert.False(MessageAnalysis.SenderMismatch(Corpus.Load("plain-utf8.eml").From.Mailboxes.Single()));
        Assert.False(MessageAnalysis.SenderMismatch(new MimeKit.MailboxAddress("support@mail.paypal.com", "x@paypal.com")));
        Assert.True(MessageAnalysis.SenderMismatch(new MimeKit.MailboxAddress("ceo@bank.co.uk", "x@other.co.uk")));
    }

    [Fact]
    public void Thread_key_is_shared_by_a_conversation()
    {
        var root = Corpus.Load("thread-1.eml");
        var reply = Corpus.Load("thread-2.eml");
        var a = MessageAnalysis.ThreadKey(root.MessageId, root.References, root.InReplyTo);
        var b = MessageAnalysis.ThreadKey(reply.MessageId, reply.References, reply.InReplyTo);
        Assert.Equal(a, b);
        Assert.StartsWith("t_", a);
    }
}

public class AttachmentTextTests
{
    [Fact]
    public void Extracts_pdf_text_layer()
    {
        var message = Corpus.Load("nested-multipart.eml");
        var pdf = message.Attachments.Single(a => a.ContentType.MimeType == "application/pdf");
        var result = AttachmentText.Extract(pdf, 1000, 10_000);
        Assert.Contains("Steuerausweis 2026", result.Text);
    }

    [Fact]
    public void Blocks_archives_and_tnef_is_unsupported()
    {
        var zip = Corpus.Load("nested-multipart.eml").Attachments.Single(a => a.ContentType.MimeType == "application/zip");
        Assert.Equal("blocked_type", AttachmentText.Extract(zip, 10, 1000).Reason);
        var tnef = Corpus.Load("tnef.eml").Attachments.Single();
        Assert.Equal("unsupported_type", AttachmentText.Extract(tnef, 10, 1000).Reason);
        Assert.True(AttachmentText.IsBlocked("invoice.xlsm", "application/octet-stream"));
        Assert.True(AttachmentText.IsBlocked("setup.exe", "application/octet-stream"));
    }

    [Fact]
    public void Extracts_csv_and_ics()
    {
        var csv = Corpus.Load("encoded-headers.eml").Attachments.Single();
        Assert.Contains("Januar,120", AttachmentText.Extract(csv, 100, 1000).Text);
        var ics = Corpus.Load("calendar-invite.eml").Attachments.Single();
        Assert.Contains("SUMMARY:Elternabend", AttachmentText.Extract(ics, 100, 1000).Text);
    }

    [Fact]
    public void Rejects_oversized()
    {
        var csv = Corpus.Load("encoded-headers.eml").Attachments.Single();
        Assert.Equal("too_large", AttachmentText.Extract(csv, AttachmentText.MaxExtractBytes + 1, 1000).Reason);
    }
}
