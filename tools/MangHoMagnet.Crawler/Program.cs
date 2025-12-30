using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MangHoMagnet.Crawler;

internal static class Program
{
    private static readonly Regex SteamLinkRegex = new Regex(
        @"steam://joinlobby/\d+/\d+/\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new Regex(
        @"<.*?>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ReplyCountRegex = new Regex(@"\d+", RegexOptions.Compiled);

    private static readonly string DefaultListUrl = "https://gall.dcinside.com/mgallery/board/lists?id=bingbong";

    private static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var listUrl = GetArg(args, "--url") ?? DefaultListUrl;
        var intervalSeconds = GetArgInt(args, "--interval", 10);
        var maxPosts = Math.Max(GetArgInt(args, "--max-posts", 50), 1);
        var cooldownSeconds = Math.Max(GetArgInt(args, "--cooldown", 60), 0);
        var iterations = GetArgInt(args, "--iterations", 0);
        var userAgent = GetArg(args, "--ua") ??
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";

        using var http = CreateHttpClient(userAgent);

        var postInfoByUrl = new Dictionary<string, PostInfo>(StringComparer.OrdinalIgnoreCase);
        var lastFetchUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var fetchCountByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var iteration = 0;
        Console.WriteLine($"[Crawler] List URL: {listUrl}");
        Console.WriteLine($"[Crawler] interval={intervalSeconds}s maxPosts={maxPosts} cooldown={cooldownSeconds}s iterations={(iterations <= 0 ? "∞" : iterations.ToString())}");

        while (iterations <= 0 || iteration < iterations)
        {
            iteration++;
            var startedUtc = DateTime.UtcNow;
            var listHtml = await FetchStringAsync(http, listUrl, "list");
            if (string.IsNullOrWhiteSpace(listHtml))
            {
                Console.WriteLine("[Crawler] List fetch failed.");
            }
            else
            {
                var postInfos = ExtractPostInfos(listHtml, maxPosts);
                var scannedPosts = 0;
                var fetchedPosts = 0;
                var skippedCooldown = 0;
                var newLinks = 0;

                foreach (var postInfo in postInfos)
                {
                    var isNewPost = !postInfoByUrl.TryGetValue(postInfo.Url, out var previous);
                    var needsFetch = isNewPost || HasListMetadataChanged(previous, postInfo);
                    postInfoByUrl[postInfo.Url] = postInfo;

                    if (!needsFetch)
                    {
                        continue;
                    }

                    if (!isNewPost && cooldownSeconds > 0 &&
                        lastFetchUtc.TryGetValue(postInfo.Url, out var lastFetch) &&
                        DateTime.UtcNow - lastFetch < TimeSpan.FromSeconds(cooldownSeconds))
                    {
                        skippedCooldown++;
                        continue;
                    }

                    var postHtml = await FetchStringAsync(http, postInfo.Url, "post");
                    if (string.IsNullOrWhiteSpace(postHtml))
                    {
                        continue;
                    }

                    scannedPosts++;
                    fetchedPosts++;
                    lastFetchUtc[postInfo.Url] = DateTime.UtcNow;
                    fetchCountByUrl[postInfo.Url] = fetchCountByUrl.TryGetValue(postInfo.Url, out var count)
                        ? count + 1
                        : 1;

                    var fullPostDate = ExtractExactPostDate(postHtml);
                    var effectivePost = string.IsNullOrWhiteSpace(fullPostDate)
                        ? postInfo
                        : postInfo.WithDate(fullPostDate);

                    foreach (var link in ExtractSteamLinks(postHtml))
                    {
                        if (seenLinks.Add(link))
                        {
                            newLinks++;
                            Console.WriteLine($"[Link] {link} | {effectivePost.Id} | {effectivePost.Title}");
                        }
                    }
                }

                var elapsed = DateTime.UtcNow - startedUtc;
                Console.WriteLine($"[Poll {iteration}] list={postInfos.Count} fetched={fetchedPosts} skippedCooldown={skippedCooldown} newLinks={newLinks} elapsed={elapsed.TotalSeconds:F1}s");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(intervalSeconds, 1)));
        }

        Console.WriteLine("[Crawler] done.");
        return 0;
    }

    private static HttpClient CreateHttpClient(string userAgent)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    private static async Task<string?> FetchStringAsync(HttpClient http, string url, string context)
    {
        try
        {
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Crawler] Failed to fetch {context}: {url} ({(int)response.StatusCode} {response.ReasonPhrase})");
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Crawler] Failed to fetch {context}: {url} ({ex.Message})");
            return null;
        }
    }

    private static List<PostInfo> ExtractPostInfos(string html, int maxPosts)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("tr.ub-content");
        var results = new List<PostInfo>();

        foreach (var row in rows)
        {
            if (results.Count >= maxPosts)
            {
                break;
            }

            var info = TryExtractPostInfo(row);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    private static PostInfo? TryExtractPostInfo(IElement row)
    {
        var titleAnchor = row.QuerySelector("td.gall_tit a");
        if (titleAnchor == null)
        {
            return null;
        }

        var href = titleAnchor.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href) || !href.Contains("board/view", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = CleanText(titleAnchor.TextContent);
        var replyCount = ExtractReplyCount(row);
        if (replyCount > 0 && !title.Contains($"[{replyCount}]", StringComparison.Ordinal))
        {
            title = $"{title} [{replyCount}]";
        }

        var postId = ExtractPostId(row);
        var author = ExtractAuthor(row);
        var postDate = ExtractPostDate(row);
        var views = ExtractViewCount(row);
        var url = NormalizeUrl(WebUtility.HtmlDecode(href));

        return new PostInfo(postId, title, author, postDate, views, url);
    }

    private static string ExtractPostId(IElement row)
    {
        var id = row.GetAttribute("data-no");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = CleanText(row.QuerySelector("td.gall_num")?.TextContent ?? string.Empty);
        }

        return id ?? string.Empty;
    }

    private static string ExtractAuthor(IElement row)
    {
        return CleanText(row.QuerySelector("td.gall_writer")?.TextContent ?? string.Empty);
    }

    private static string ExtractPostDate(IElement row)
    {
        var dateCell = row.QuerySelector("td.gall_date");
        if (dateCell == null)
        {
            return string.Empty;
        }

        var full = dateCell.GetAttribute("title") ?? string.Empty;
        var shortText = dateCell.TextContent ?? string.Empty;
        return FormatDate(full, shortText);
    }

    private static string ExtractViewCount(IElement row)
    {
        var cleaned = CleanText(row.QuerySelector("td.gall_count")?.TextContent ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }

        var digitsOnly = new string(cleaned.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digitsOnly) ? cleaned : digitsOnly;
    }

    private static int ExtractReplyCount(IElement row)
    {
        var text = row.QuerySelector("span.reply_num")?.TextContent;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var match = ReplyCountRegex.Match(text);
        return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
    }

    private static string NormalizeUrl(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        if (href.StartsWith("//", StringComparison.OrdinalIgnoreCase))
        {
            return $"https:{href}";
        }

        if (href.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://gall.dcinside.com{href}";
        }

        return $"https://gall.dcinside.com/{href}";
    }

    private static string CleanText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var noTags = TagRegex.Replace(html, string.Empty);
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    private static string FormatDate(string full, string shortText)
    {
        var trimmedFull = (full ?? string.Empty).Trim();
        if (DateTime.TryParseExact(
                trimmedFull,
                new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed) ||
            DateTime.TryParse(trimmedFull, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed) ||
            DateTime.TryParse(trimmedFull, out parsed))
        {
            return parsed.ToString("MM-dd HH:mm");
        }

        return CleanText(shortText);
    }

    private static string ExtractExactPostDate(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        foreach (var element in document.QuerySelectorAll(".gall_date"))
        {
            var candidate = element.GetAttribute("title");
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = element.TextContent;
            }

            var normalizedCandidate = CleanText(candidate ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                continue;
            }

            if (TryParseExactDate(normalizedCandidate, out var parsed))
            {
                return parsed.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            var normalized = NormalizeExactDateText(normalizedCandidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static bool TryParseExactDate(string value, out DateTime parsed)
    {
        return DateTime.TryParseExact(
                   value,
                   new[]
                   {
                       "yyyy-MM-dd HH:mm:ss",
                       "yyyy-MM-dd HH:mm",
                       "yyyy.MM.dd HH:mm:ss",
                       "yyyy.MM.dd HH:mm",
                       "yyyy/MM/dd HH:mm:ss",
                       "yyyy/MM/dd HH:mm"
                   },
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out parsed) ||
               DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed);
    }

    private static string NormalizeExactDateText(string value)
    {
        var match = Regex.Match(value, @"(?<date>\d{4}[./-]\d{2}[./-]\d{2})\s+(?<time>\d{2}:\d{2}(?::\d{2})?)");
        if (!match.Success)
        {
            return value;
        }

        var datePart = match.Groups["date"].Value.Replace('-', '.').Replace('/', '.');
        var timePart = match.Groups["time"].Value;
        if (timePart.Length == 5)
        {
            timePart += ":00";
        }

        return $"{datePart} {timePart}";
    }

    private static IEnumerable<string> ExtractSteamLinks(string html)
    {
        foreach (Match match in SteamLinkRegex.Matches(html))
        {
            yield return match.Value;
        }
    }

    private static bool HasListMetadataChanged(PostInfo previous, PostInfo current)
    {
        if (!string.Equals(previous.Title, current.Title, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Author, current.Author, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Date, current.Date, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Views, current.Views, StringComparison.Ordinal))
        {
            if (TryParseViewCount(previous.Views, out var previousViews) &&
                TryParseViewCount(current.Views, out var currentViews))
            {
                return currentViews - previousViews >= 2;
            }

            return true;
        }

        return false;
    }

    private static bool TryParseViewCount(string value, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return false;
        }

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
    }

    private static string? GetArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int GetArgInt(string[] args, string key, int fallback)
    {
        var raw = GetArg(args, key);
        return raw != null && int.TryParse(raw, out var value) ? value : fallback;
    }

    private sealed class PostInfo
    {
        public PostInfo(string id, string title, string author, string date, string views, string url)
        {
            Id = id;
            Url = url;
            Title = title;
            Author = author;
            Date = date;
            Views = views;
        }

        public string Id { get; }
        public string Url { get; }
        public string Title { get; }
        public string Author { get; }
        public string Date { get; }
        public string Views { get; }

        public PostInfo WithDate(string date)
        {
            if (string.IsNullOrWhiteSpace(date) || date == Date)
            {
                return this;
            }

            return new PostInfo(Id, Title, Author, date, Views, Url);
        }
    }
}
