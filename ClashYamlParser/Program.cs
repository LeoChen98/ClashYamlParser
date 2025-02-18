using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

ConcurrentQueue<string> log_temp = new();
string log_file = $"logs\\{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.log";
List<string> blacklist = [];

await log($"{Assembly.GetExecutingAssembly().GetName().Version}", "version");
Timer log_wraper = new(async (o) => { await WrapLogAsync(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
Timer blacklist_refresher = new(async (o) => { await LoadBlacklist(); }, null, TimeSpan.Zero, TimeSpan.FromMinutes(5));

HttpListener server = new HttpListener();
server.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

foreach (string domain in await File.ReadAllLinesAsync("domains.txt"))
{
    server.Prefixes.Add(domain);
}

await log("Sever stared.");
server.Start();
server.BeginGetContext(ProcessRequest, server);

await Task.Run(async () =>
{
    while (Console.ReadLine() != "/exit")
    {
    }

    await log("Application exited.", flush: true);
});

async Task LoadBlacklist()
{
    if (!File.Exists("blacklist.txt"))
    {
        return;
    }

    string[] backup = new string[blacklist.Capacity];
    blacklist.CopyTo(backup);
    blacklist = new(blacklist.Capacity);
    try
    {
        using HttpClient httpClient = new();
        Regex regex = RegexDomain();
        foreach (string line in await File.ReadAllLinesAsync("blacklist.txt"))
        {
            HttpResponseMessage response = await httpClient.GetAsync(line);
            if (response.IsSuccessStatusCode)
            {
                foreach (Match m in regex.Matches(await response.Content.ReadAsStringAsync()))
                {
                    blacklist.Add(string.Format("DOMAIN-SUFFIX,{0},REJECT", m.Value));
                }
            }
        }
    }
    catch
    {
        blacklist = [.. backup];
    }
}

async Task WrapLogAsync()
{
    StringBuilder sb = new StringBuilder();
    while (!log_temp.IsEmpty)
    {
        if (log_temp.TryDequeue(out string? line))
        {
            sb.AppendLine(line);
        }
    }
    if (!Directory.Exists("logs"))
    {
        Directory.CreateDirectory("logs");
    }
    await File.AppendAllTextAsync(log_file, sb.ToString());
}

async void ProcessRequest(IAsyncResult ar)
{
    HttpListenerContext context = server.EndGetContext(ar);
    server.BeginGetContext(ProcessRequest, server);
    try
    {
        ArgumentNullException.ThrowIfNull(context, "context");
        ArgumentNullException.ThrowIfNull(context.Request, "context.Request");
        ArgumentNullException.ThrowIfNull(context.Request.Url, "context.Request.Url");
        await log($"Request: {context.Request.Url}");
        switch (context.Request.Url.AbsolutePath.ToLower())
        {
            case "/clash":
            case "/clash/":
                string? subscribe_url = context.Request.QueryString["url"];
                ArgumentException.ThrowIfNullOrWhiteSpace(subscribe_url, "url");
                var origin_response = await GetOriginYamlAsync(subscribe_url);
                string origin_yaml = origin_response.Item1;
                string parser_yaml = await File.ReadAllTextAsync("parser.yaml", Encoding.UTF8);
                string result_yaml = CombineYaml(origin_yaml, parser_yaml);
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Subscription-Userinfo", origin_response.Item3);
                SendResponseString(context, result_yaml, contentType: "application/octet-stream", contentDisposition: $"attachment; filename={origin_response.Item2}");
                break;

            default:
                SendResponseString(context, "API Not Found!", 404);
                await log($"Bad request: API not found.{context.Request.Url}", "error");
                break;
        }
    }
    catch (ArgumentException ex)
    {
        SendResponseString(context, $"Bad request: {ex.Message}", 400);
        await log($"Bad request: ArgumentException.{ex.Message}", "error");
    }
    catch (Exception ex)
    {
        SendResponseString(context, $"Internal Exception:{ex.Message}", 500);
        await log($"Internal Exception:{ex.Message}", "error");
    }
}
void SendResponse(HttpListenerContext context, byte[]? data = null, int statusCode = 200, string contentType = "text/plain", string strEncoding = "", string contentDisposition = "")
{
    context.Response.StatusCode = statusCode;

    if (!string.IsNullOrEmpty(contentDisposition))
    {
        context.Response.AddHeader("Content-Disposition", contentDisposition);
    }

    if (string.IsNullOrEmpty(strEncoding))
    {
        context.Response.ContentType = $"{contentType}";
    }
    else
    {
        context.Response.ContentType = $"{contentType}; chartset={strEncoding}";
    }

    if (data is not null && data.Length > 0)
    {
        context.Response.OutputStream.Write(data, 0, data.Length);
    }

    context.Response.Close();
}

async Task log(string logData, string logLevel = "info", bool flush = false)
{
    //string log_line = $"[{DateTime.UtcNow:yyyy/MM/dd hh:mm:ss}z][{log_level}]{log_data}";
    string log_line = string.Format(string.Concat("[{0:yyyy/MM/dd HH:mm:ss}z][{1}]{2}"), DateTime.UtcNow, logLevel, logData);
    Console.WriteLine(log_line);
    log_temp.Enqueue(log_line);

    if (logLevel != "info" || log_temp.Count > 10 || flush)
    {
        await WrapLogAsync();
    }
}

void SendResponseString(HttpListenerContext context, string? strData = null, int statusCode = 200, string contentType = "text/plain", string strEncoding = "utf-8", string contentDisposition = "")
{
    Encoding encoding;
    try
    {
        encoding = Encoding.GetEncoding(strEncoding);
    }
    catch
    {
        encoding = Encoding.UTF8;
    }

    byte[]? data = null;
    if (!string.IsNullOrEmpty(strData))
    {
        strData = Regex.Replace(strData, @"\\U([0-9A-F]{8})", match =>
        {
            int codePoint = int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
            return char.ConvertFromUtf32(codePoint);
        });

        data = encoding.GetBytes(strData);
    }

    SendResponse(context, data, statusCode, contentType, strEncoding, contentDisposition);
}

async Task<(string, string, string)> GetOriginYamlAsync(string url)
{
    using HttpClient httpClient = new();
    var response = await httpClient.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentDisposition?.FileName ?? "clash.xaml", response.Headers.GetValues("Subscription-Userinfo").First());
}

string CombineYaml(string originYaml, string parserYaml)
{
    var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

    var serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    var originDict = deserializer.Deserialize<Dictionary<string, object>>(originYaml) ?? new();

    var parserList = deserializer.Deserialize<Dictionary<string, object>>(parserYaml)["parsers"] as List<object>;
    if (parserList is null)
    {
        return serializer.Serialize(originDict);
    }

    var parserDict = (parserList[0] as Dictionary<object, object>)?["yaml"] as Dictionary<object, object> ?? null;

    if (parserDict is null)
    {
        return serializer.Serialize(originDict);
    }

    AddAdBlocker(ref originDict);

    MergeConfig("rules", parserDict, ref originDict);
    MergeConfig("proxies", parserDict, ref originDict);
    MergeGroups( parserDict, ref originDict);
    MergeConfig("proxy-providers", parserDict, ref originDict);
    MergeConfig("rule-providers", parserDict, ref originDict);
    MixObject(parserDict, ref originDict);

    return serializer.Serialize(originDict);
}

void AddAdBlocker(ref Dictionary<string, object> originDict)
{
    if (!originDict.TryGetValue("rules", out object? value))
    {
        originDict.Add("rules", blacklist);
    }
    else
    {
        (value as List<object>)!.InsertRange(0, blacklist);
    }
}

void MergeGroups(Dictionary<object, object> parserDict, ref Dictionary<string, object> originDict)
{
    MergeConfig("proxy-groups",parserDict,ref originDict);

    if (!originDict.ContainsKey("proxy-groups")) return;
    if (originDict["proxy-groups"] is not List<Dictionary<string, object>> proxyGroups) return;
    
    var mergedGroups = new Dictionary<string, Dictionary<string, object>>();
    foreach (var group in proxyGroups)
    {
        if (group.TryGetValue("name", out var name) && name is string groupName)
        {
            if (mergedGroups.ContainsKey(groupName))
            {
                if( group["proxies"] is not string[]) continue;
                foreach (var kvp in (group["proxies"] as string[])!)
                {
                    if(mergedGroups[groupName]["proxies"] is not List<string>)
                    {
                        mergedGroups[groupName]["proxies"] = new List<string>();
                    }
                    (mergedGroups[groupName]["proxies"] as List<string>)!.Add(kvp) ;
                }
            }
            else
            {
                mergedGroups[groupName] = new Dictionary<string, object>(group);
            }
        }
    }
    originDict["proxy-groups"] = mergedGroups.Values.ToList();
}

void MergeConfig(string type, Dictionary<object, object> parserDict, ref Dictionary<string, object> originDict)
{
    if (originDict.ContainsKey(type))
    {
        if (parserDict.ContainsKey($"prepend-{type}") && parserDict[$"prepend-{type}"] is not null)
        {
            (originDict[type] as List<object>)!.InsertRange(0, (parserDict[$"prepend-{type}"] as List<object>)!);
        }
        if (parserDict.ContainsKey($"append-{type}") && parserDict[$"append-{type}"] is not null)
        {
            (originDict[type] as List<object>)!.AddRange((parserDict[$"append-{type}"] as List<object>)!);
        }
        if (parserDict.ContainsKey($"mix-{type}") && parserDict[$"mix-{type}"] is not null)
        {
            originDict[type] = (originDict[type] as Dictionary<object, object>)!.Concat((parserDict[$"mix-{type}"] as Dictionary<object, object>)!);
        }
    }
    else
    {
        if (((!parserDict.ContainsKey($"prepend-{type}") || parserDict[$"prepend-{type}"] is null)) && (parserDict.ContainsKey($"append-{type}") && parserDict[$"append-{type}"] is not null))
        {
            originDict.Add(type, (parserDict[$"append-{type}"] as List<object>)!);
        }
        else if ((parserDict.ContainsKey($"prepend-{type}") && parserDict[$"prepend-{type}"] is not null) && (!parserDict.ContainsKey($"append-{type}") || parserDict[$"append-{type}"] is null))
        {
            originDict.Add(type, (parserDict[$"prepend-{type}"] as List<object>)!);
        }
        else if ((parserDict.ContainsKey($"prepend-{type}") && parserDict[$"prepend-{type}"] is not null) && (parserDict.ContainsKey($"append-{type}") && parserDict[$"append-{type}"] is not null))
        {
            originDict.Add(type, (parserDict[$"prepend-{type}"] as List<object>)!.Concat((parserDict[$"append-{type}"] as List<object>)!));
        }

        if (parserDict.ContainsKey($"mix-{type}") && parserDict[$"mix-{type}"] is not null)
        {
            originDict.Add(type, (parserDict[$"mix-{type}"] as Dictionary<object, object>)!);
        }
    }
}

void MixObject(Dictionary<object, object> parserDict, ref Dictionary<string, object> originDict)
{
    if (!parserDict.TryGetValue("mix-object", out var mix_object))
    {
        return;
    }

    if (mix_object is not Dictionary<object, List<object>> objs || objs.Count == 0)
    {
        return;
    }

    foreach (var obj in objs)
    {
        if (originDict.ContainsKey((string)obj.Key))
        {
            if (originDict[((string)obj.Key)] is List<object> values)
            {
                values.AddRange(obj.Value);
                continue;
            }
            log($"mix-object: {obj.Key} failed, trying to create the note.", "warning", true).GetAwaiter().GetResult();
            originDict[((string)obj.Key)] = obj.Value;
            continue;
        }

        if (!originDict.TryAdd((string)obj.Key, obj.Value))
        {
            log($"mix-object: {obj.Key} failed, skipped.", "warning", true).GetAwaiter().GetResult();
        }
    }
}

partial class Program
{
    #region Private Methods

    [GeneratedRegex(@"\b((?:(?:[a-zA-Z0-9-]{1,63}\.)+(?:[a-zA-Z]{2,})))\b")]
    private static partial Regex RegexDomain();

    #endregion Private Methods
}