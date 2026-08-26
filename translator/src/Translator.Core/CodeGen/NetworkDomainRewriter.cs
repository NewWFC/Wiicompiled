using System.Text;
using System.Text.RegularExpressions;

namespace Translator.Core.CodeGen;

/// <summary>
/// Rewrites embedded NUL-terminated ASCII hostnames inside DOL/REL data-section bytes in place -
/// e.g. redirecting the game's hardcoded "nintendowifi.net"/"gamespy.net" WFC endpoints to a
/// replacement domain. Every affected string keeps its original start address (so any absolute
/// pointer elsewhere in the binary that references it stays valid): a subdomain label before the
/// match is left untouched, the matched anchor is swapped for the replacement, any path/query that
/// followed the anchor is shifted left to sit directly after it, and the freed tail bytes up to the
/// original NUL terminator are zeroed. Nothing outside that one string's original byte span is ever
/// touched.
///
/// A literal "https://" immediately before a rewritten hostname is also downgraded to "http://":
/// a replacement WFC server is under no obligation to terminate TLS, and forcing the original
/// scheme would leave the game trying to negotiate HTTPS against a plain HTTP listener. This only
/// ever touches strings that already contain a rewritten hostname, never scheme text elsewhere.
/// </summary>
public static class NetworkDomainRewriter
{
    public sealed record DomainRewrite(string From, string To)
    {
        public DomainRewrite() : this("", "") { }
    }

    public sealed record Match(int Offset, string Original, string Replacement);

    public static (byte[] Data, IReadOnlyList<Match> Matches) Rewrite(
        ReadOnlySpan<byte> data, IReadOnlyList<DomainRewrite> rewrites)
    {
        var buffer = data.ToArray();
        var matches = new List<Match>();

        foreach (var rewrite in rewrites)
        {
            if (string.IsNullOrEmpty(rewrite.From))
            {
                throw new ArgumentException("A domain rewrite needs a non-empty 'From' value.");
            }

            var fromBytes = Encoding.ASCII.GetBytes(rewrite.From);
            var toBytes = Encoding.ASCII.GetBytes(rewrite.To);
            if (toBytes.Length > fromBytes.Length)
            {
                throw new ArgumentException(
                    $"Replacement '{rewrite.To}' ({toBytes.Length} bytes) is longer than " +
                    $"'{rewrite.From}' ({fromBytes.Length} bytes); an embedded string cannot grow " +
                    "in place without moving every absolute pointer that references it.");
            }

            var searchFrom = 0;
            while (true)
            {
                var index = IndexOf(buffer, fromBytes, searchFrom);
                if (index < 0)
                {
                    break;
                }
                // Keep scanning from the very next byte - two matches could legitimately overlap
                // in pathological input, and a match this pass already rewrote is now a different
                // byte sequence anyway, so re-scanning past just the match start is always correct.
                searchFrom = index + 1;

                var anchorEnd = index + fromBytes.Length;
                var stringEnd = anchorEnd;
                while (stringEnd < buffer.Length && buffer[stringEnd] != 0)
                {
                    stringEnd++;
                }
                if (stringEnd >= buffer.Length)
                {
                    // Ran off the end of the section without finding a terminator - not a proper
                    // embedded C string (or it's the very last, unterminated bytes of the section);
                    // too risky to touch.
                    continue;
                }

                var stringStart = FindStringStart(buffer, index);
                var original = Encoding.ASCII.GetString(buffer, stringStart, stringEnd - stringStart);

                var prefix = DowngradeHttps(buffer, stringStart, index);
                var suffix = buffer.AsSpan(anchorEnd, stringEnd - anchorEnd).ToArray();
                var newLength = prefix.Length + toBytes.Length + suffix.Length;
                if (newLength > stringEnd - stringStart)
                {
                    // "https://" -> "http://" only ever shrinks, so this should be unreachable given
                    // the From/To length check above, but never write past what was verified safe.
                    continue;
                }

                var cursor = stringStart;
                Array.Copy(prefix, 0, buffer, cursor, prefix.Length);
                cursor += prefix.Length;
                Array.Copy(toBytes, 0, buffer, cursor, toBytes.Length);
                cursor += toBytes.Length;
                Array.Copy(suffix, 0, buffer, cursor, suffix.Length);
                cursor += suffix.Length;
                Array.Clear(buffer, cursor, stringEnd - cursor);

                var replacement = Encoding.ASCII.GetString(buffer, stringStart, cursor - stringStart);
                matches.Add(new Match(stringStart, original, replacement));
            }
        }

        return (buffer, matches);
    }

    private static readonly Regex HttpsScheme = new("https://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Case-insensitive "https://" -&gt; "http://" over buffer[start..end), read from the
    /// original (not-yet-mutated) bytes. Only ever shrinks, since "http://" is one byte shorter.</summary>
    private static byte[] DowngradeHttps(byte[] buffer, int start, int end)
    {
        var text = Encoding.ASCII.GetString(buffer, start, end - start);
        var downgraded = HttpsScheme.Replace(text, "http://");
        return Encoding.ASCII.GetBytes(downgraded);
    }

    private static int FindStringStart(byte[] buffer, int fromIndex)
    {
        var i = fromIndex;
        while (i > 0 && buffer[i - 1] != 0 && buffer[i - 1] >= 0x20 && buffer[i - 1] < 0x7F)
        {
            i--;
        }
        return i;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        if (needle.Length == 0 || from < 0)
        {
            return -1;
        }
        var last = haystack.Length - needle.Length;
        for (var i = from; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return i;
            }
        }
        return -1;
    }
}
