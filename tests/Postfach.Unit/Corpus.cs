using MimeKit;

namespace Postfach.Unit;

internal static class Corpus
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    public static MimeMessage Load(string name) => MimeMessage.Load(PathOf(name));
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
