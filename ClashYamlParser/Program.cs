
using System.Net;
using System.Text;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;

HttpListener server = new HttpListener();
server.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

foreach (string domain in await File.ReadAllLinesAsync("domains.txt"))
{
    server.Prefixes.Add(domain);
}

server.Start();
server.BeginGetContext(ProcessRequest, server);

await Task.Run(() =>
{
    while (Console.ReadLine() != "/exit")
    {
    }
});

async void ProcessRequest(IAsyncResult ar)
{
    HttpListenerContext context = server.EndGetContext(ar);
    server.BeginGetContext(ProcessRequest, server);
    try
    {
        ArgumentNullException.ThrowIfNull(context, "context");
        ArgumentNullException.ThrowIfNull(context.Request, "context.Request");
        ArgumentNullException.ThrowIfNull(context.Request.Url, "context.Request.Url");
        switch (context.Request.Url.AbsolutePath.ToLower())
        {
            case "/clash":
            case "/clash/":
                string? subscribe_url = context.Request.QueryString["url"];
                ArgumentException.ThrowIfNullOrWhiteSpace(subscribe_url, "url");
                string origin_YAML = await GetOriginYAMLAsync(subscribe_url);
                string parser_YAML = await File.ReadAllTextAsync("parser.yaml");
                string result_YAML = CombineYAML(origin_YAML, parser_YAML);
                SendResponseString(context, result_YAML, content_type: "application/octet-stream");
                break;

            default:
                SendResponseString(context, "API Not Found!", 404);
                break;
        }
    }
    catch (ArgumentException ex)
    {
        SendResponseString(context, $"Bad request: {ex.Message}", 400);
    }
    catch (Exception ex)
    {
        SendResponseString(context, $"Internal Exception:{ex.Message}", 500);
    }
}
void SendResponse(HttpListenerContext context, byte[]? data = null, int status_code = 200, string content_type = "text/plain", string str_encoding = "")
{
    context.Response.StatusCode = status_code;

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

void SendResponseString(HttpListenerContext context, string? str_data = null, int status_code = 200, string content_type = "text/plain", string str_encoding = "utf-8")
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
        data = encoding.GetBytes(str_data);
    }

    SendResponse(context, data, status_code, content_type, str_encoding);
}

async Task<string> GetOriginYAMLAsync(string url)
{
    using (HttpClient httpClient = new HttpClient())
    {
        return await (await httpClient.GetAsync(url)).Content.ReadAsStringAsync();
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
    var parserDict = deserializer.Deserialize<List<object>>(parser_YAML)[0] as Dictionary<string, object>;

    if (parserDict is null)
    {
        return serializer.Serialize(originDict);
    }

    MergeConfig("rules", parserDict, ref originDict);
    MergeConfig("proxies", parserDict, ref originDict);
    MergeConfig("proxy-groups", parserDict, ref originDict);

    return serializer.Serialize(originDict);
}

void MergeConfig(string type, Dictionary<string, object> parserDict, ref Dictionary<string, object> originDict)
{
    if (originDict.ContainsKey(type))
    {
        if (parserDict[$"prepend-{type}"] is not null)
        {
            (originDict[type] as List<object>)!.InsertRange(0, (parserDict[$"prepend-{type}"] as object[])!);
        }
        if (parserDict[$"append-{type}"] is not null)
        {
            (originDict[type] as List<object>)!.AddRange((parserDict[$"append-{type}"] as object[])!);
        }

    }
    else
    {
        if (parserDict[$"prepend-{type}"] is null && parserDict[$"append-{type}"] is not null)
        {
            originDict.Add(type, (parserDict[$"append-{type}"] as object[])!);
        }
        else if (parserDict[$"prepend-{type}"] is not null && parserDict[$"append-{type}"] is null)
        {
            originDict.Add(type, (parserDict[$"prepend-{type}"] as object[])!);
        }
        else if (parserDict[$"prepend-{type}"] is not null && parserDict[$"append-{type}"] is not null)
        {
            originDict.Add(type, (parserDict[$"prepend-{type}"] as object[])!.Concat((parserDict[$"append-{type}"] as object[])!));
        }
    }
}