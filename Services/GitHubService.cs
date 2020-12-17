using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.Hosting;

using RadzenBlazorDemos.Models.GitHub;

namespace RadzenBlazorDemos.Services
{
    class Link
    {
        public string Next { get; set; }
        public string Last { get; set; }

        public int NextPage { get; set; }
        public int LastPage { get; set; }

        private static int ExtractPage(string value)
        {
            var match = Regex.Match(value, "page=(?<page>\\d+)");

            if (match != null)
            {
                return Convert.ToInt32(match.Groups["page"].Value);
            }

            return 0;
        }

        public static Link FromHeader(IEnumerable<string> header)
        {
            var result = new Link();

            var links = String.Join("", header).Split(',');

            foreach (var link in links)
            {
                var rel = Regex.Match(link, "(?<=rel=\").+?(?=\")", RegexOptions.IgnoreCase);
                var value = Regex.Match(link, "(?<=<).+?(?=>)", RegexOptions.IgnoreCase);

                if (rel.Success && value.Success)
                {
                    if (rel.Value == "next")
                    {
                        result.Next = value.Value;
                        result.NextPage = ExtractPage(result.Next);
                    }
                    if (rel.Value == "last")
                    {
                        result.Last = value.Value;
                        result.LastPage = ExtractPage(result.Last);
                    }
                }
            }

            return result;
        }
    }

    class IssueCache
    {
        [JsonPropertyName("date")]
        public DateTime Date { get; set; }

        [JsonPropertyName("issues")]
        public IEnumerable<Issue> Issues { get; set; }
    }

    public class FetchProgressEventArgs
    {
        public int Total { get; set; }
        public int Current { get; set; }
    }

    public class GitHubService
    {
        private readonly IWebHostEnvironment hostEnvironment;

        private readonly JsonSerializerOptions options;

        public Action<FetchProgressEventArgs> OnProgress;

        public GitHubService(IWebHostEnvironment hostEnvironment)
        {
            this.hostEnvironment = hostEnvironment;
            options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }

        public async Task<IEnumerable<Issue>> GetIssues(DateTime date)
        {
            var cacheFile = Path.Combine(hostEnvironment.WebRootPath, "issues.json");
            var lastMonth = new DateTime(date.Year, date.Month - 1, 1);

            IEnumerable<Issue> issues = null;

            if (File.Exists(cacheFile))
            {
                var json = await File.ReadAllTextAsync(cacheFile);
                var cache = JsonSerializer.Deserialize<IssueCache>(json, options);

                if (date.Subtract(cache.Date).TotalHours < 24)
                {
                    issues = cache.Issues;
                }
            }

            if (issues == null)
            {
                issues = await FetchIssues(lastMonth);

                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new IssueCache { Date = date, Issues = issues }, options));
            }

            return issues.Where(issue => issue.CreatedAt >= lastMonth).OrderBy(issue => issue.CreatedAt);
        }

        private async Task<IEnumerable<Issue>> FetchIssues(DateTime since)
        {
            var issues = new List<Issue>();

            using (var http = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/dotnet/aspnetcore/issues?state=all&labels=area-blazor&since={since.ToString("yyyy-MM-ddThh:mm:ssZ")}");
                request.Headers.Add("User-Agent", "Radzen");

                var response = await http.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    using var responseStream = await response.Content.ReadAsStreamAsync();
                    var page = await JsonSerializer.DeserializeAsync<IEnumerable<Issue>>(responseStream, options);
                    issues.AddRange(page);
                    var link = Link.FromHeader(response.Headers.GetValues("Link"));
                    OnProgress?.Invoke(new FetchProgressEventArgs { Current = 1, Total = link.LastPage });

                    while (link.Next != null)
                    {
                        OnProgress?.Invoke(new FetchProgressEventArgs { Current = link.NextPage, Total = link.LastPage });
                        request = new HttpRequestMessage(HttpMethod.Get, link.Next);
                        request.Headers.Add("User-Agent", "Radzen");

                        response = await http.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync())
                            {
                                page = await JsonSerializer.DeserializeAsync<IEnumerable<Issue>>(stream, options);
                                issues.AddRange(page);
                            }

                            link = Link.FromHeader(response.Headers.GetValues("Link"));
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    throw new ApplicationException(response.ReasonPhrase);
                }
            }

            return issues;
        }
    }
}