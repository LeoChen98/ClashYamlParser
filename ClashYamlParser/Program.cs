using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

ConcurrentQueue<string> log_temp = new();
string log_file = $"logs\\{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.log";

await log($"{Assembly.GetExecutingAssembly().GetName().Version}", "version");
Timer log_wraper = new(async (o) => { await WrapLogAsync(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

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

async Task WrapLogAsync()
{
    StringBuilder sb = new StringBuilder();
    while (log_temp.Count > 0)
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
                var origin_response = await GetOriginYAMLAsync(subscribe_url);
                string origin_YAML = origin_response.Item1;
                string parser_YAML = await File.ReadAllTextAsync("parser.yaml", Encoding.UTF8);
                string result_YAML = CombineYAML(origin_YAML, parser_YAML);
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Subscription-Userinfo", origin_response.Item3);
                SendResponseString(context, result_YAML, content_type: "application/octet-stream", content_disposition: $"attachment; filename={origin_response.Item2}");
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
void SendResponse(HttpListenerContext context, byte[]? data = null, int status_code = 200, string content_type = "text/plain", string str_encoding = "", string content_disposition = "")
{
    context.Response.StatusCode = status_code;

    if (!string.IsNullOrEmpty(content_disposition))
    {
        context.Response.AddHeader("Content-Disposition", content_disposition);
    }

    if (string.IsNullOrEmpty(str_encoding))
    {
        context.Response.ContentType = $"{content_type}";
    }
    else
    {
        context.Response.ContentType = $"{content_type}; chartset={str_encoding}";
    }

    if (data is not null && data.Length > 0)
    {
        context.Response.OutputStream.Write(data, 0, data.Length);
    }

    context.Response.Close();
}

async Task log(string log_data, string log_level = "info", bool flush = false)
{
    //string log_line = $"[{DateTime.UtcNow:yyyy/MM/dd hh:mm:ss}z][{log_level}]{log_data}";
    string log_line = string.Format(string.Concat("[{0:yyyy/MM/dd HH:mm:ss}z][{1}]{2}"), DateTime.UtcNow, log_level, log_data);
    Console.WriteLine(log_line);
    log_temp.Enqueue(log_line);

    if (log_level != "info" || log_temp.Count > 10 || flush)
    {
        await WrapLogAsync();
    }
}

void SendResponseString(HttpListenerContext context, string? str_data = null, int status_code = 200, string content_type = "text/plain", string str_encoding = "utf-8", string content_disposition = "")
{
    Encoding encoding;
    try
    {
        encoding = Encoding.GetEncoding(str_encoding);
    }
    catch
    {
        encoding = Encoding.UTF8;
    }

    byte[]? data = null;
    if (!string.IsNullOrEmpty(str_data))
    {
        str_data = Regex.Replace(str_data, @"\\U([0-9A-F]{8})", match =>
        {
            int codePoint = int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
            return char.ConvertFromUtf32(codePoint);
        });

        data = encoding.GetBytes(str_data);
    }

    SendResponse(context, data, status_code, content_type, str_encoding, content_disposition);
}

async Task<(string, string, string)> GetOriginYAMLAsync(string url)
{
    using (HttpClient httpClient = new HttpClient())
    {
        var response = await httpClient.GetAsync(url);
        return (await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentDisposition?.FileName ?? "clash.xaml", response.Headers.GetValues("Subscription-Userinfo").First());
    }
}

string CombineYAML(string origin_YAML, string parser_YAML)
{
    var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

    var serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    var originDict = deserializer.Deserialize<Dictionary<string, object>>(origin_YAML) ?? new();

    var parserList = deserializer.Deserialize<Dictionary<string, object>>(parser_YAML)["parsers"] as List<object>;
    if (parserList is null)
    {
        return serializer.Serialize(originDict);
    }

    var parserDict = (parserList[0] as Dictionary<object, object>)?["yaml"] as Dictionary<object, object> ?? null;

    if (parserDict is null)
    {
        return serializer.Serialize(originDict);
    }

    MergeConfig("rules", parserDict, ref originDict);
    MergeConfig("proxies", parserDict, ref originDict);
    MergeConfig("proxy-groups", parserDict, ref originDict);
    MergeConfig("proxy-providers", parserDict, ref originDict);
    MergeConfig("rule-providers", parserDict, ref originDict);
    MixObject(parserDict, ref originDict);

    return serializer.Serialize(originDict);
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
    if (!parserDict.ContainsKey("mix-object"))
    {
        return;
    }

    if (parserDict["mix-object"] is not Dictionary<object, List<object>> objs || objs.Count == 0)
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