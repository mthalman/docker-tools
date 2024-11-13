using System.Collections.Concurrent;
using System.CommandLine;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Containers.ContainerRegistry;
using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Valleysoft.DockerRegistryClient;

ConcurrentBag<string> failedDigests = new();

Dictionary<string, DateOnly> eolDates = new()
{
    { "7.0", new DateOnly(2024, 5, 14) },
    { "6.0", new DateOnly(2024, 11, 12) },
    { "5.0", new DateOnly(2022, 5, 10) },
    { "3.1", new DateOnly(2022, 12, 13) },
    { "3.0", new DateOnly(2020, 3, 3) },
    { "2.2", new DateOnly(2019, 12, 23) },
    { "2.1", new DateOnly(2021, 8, 21) },
    { "2.0", new DateOnly(2018, 10, 1) },
    { "1.1", new DateOnly(2019, 6, 27) },
    { "1.0", new DateOnly(2019, 6, 27) },
};

RootCommand rootCmd = new("CLI for checking EOL annotations of .NET container images");
var eolDataPathArg = new Argument<string>("--eol-data-path", "Path to the EOL data output file");
var repoArg = new Argument<string>("--repo", "Repository name with wildcard support");
var queryKustoOption = new Option<bool>("--kusto", "Query Kusto for existing EOL data");
var imageInfoPathOption = new Option<string?>("--image-info-path", "Path to the image info file");
var checkDotNetVersionOption = new Option<bool>("--check-dotnet-version", "Check the .NET version of the tag");
rootCmd.AddArgument(eolDataPathArg);
rootCmd.AddArgument(repoArg);
rootCmd.AddOption(queryKustoOption);
rootCmd.AddOption(imageInfoPathOption);
rootCmd.AddOption(checkDotNetVersionOption);
rootCmd.SetHandler(
    Execute,
    eolDataPathArg,
    repoArg,
    queryKustoOption,
    imageInfoPathOption,
    checkDotNetVersionOption);

return rootCmd.Invoke(args);

void Execute(string outputPath, string repo, bool queryKusto, string? imageInfoPath, bool checkDotNetVersion)
{
    ExecuteAsync(outputPath, repo, queryKusto, imageInfoPath, checkDotNetVersion).Wait();
}

async Task ExecuteAsync(string outputPath, string repoName, bool queryKusto, string? imageInfoPath, bool checkDotNetVersion)
{
    Dictionary<string, DigestInfo> kustoDigests = [];
    if (queryKusto)
    {
        kustoDigests = GetKustoData(repoName);
    }

    var nonKustoDigests = await GetNonKustoDataAsync(repoName, kustoDigests, checkDotNetVersion);

    IEnumerable<DigestInfo> digests = kustoDigests.Select(val => val.Value);
    digests = await FilterNonMarDigestsAsync(digests);
    digests = digests.Union(nonKustoDigests);

    if (imageInfoPath is not null)
    {
        digests = FilterImageInfoDigests(digests, imageInfoPath, repoName);
    }

    digests = digests.OrderBy(row => row.Digest);

    EolAnnotationsData eolAnnotationsData = new();
    
    foreach (var digestInfo in digests)
    {
        eolAnnotationsData.EolDigests.Add(new EolDigestData { Digest = digestInfo.Digest, EolDate = digestInfo.EolDate, Tags = [digestInfo.Dockerfile ?? digestInfo.ProductVersion?.ToString()] });
    }

    string json = System.Text.Json.JsonSerializer.Serialize(eolAnnotationsData, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outputPath, json);

    if (failedDigests.Count > 0)
    {
        Console.WriteLine("Failed digests:");
        foreach (var digest in failedDigests)
        {
            Console.WriteLine(digest);
        }
    }
}

IEnumerable<DigestInfo> FilterImageInfoDigests(IEnumerable<DigestInfo> digests, string imageInfoPath, string repoName)
{
    HashSet<string> filteredDigests = [];
    JObject imageInfo = (JObject)JsonConvert.DeserializeObject(File.ReadAllText(imageInfoPath));
    JObject repo = (JObject)imageInfo["repos"].First(repo => repo["repo"].ToString() == repoName);
    foreach (JObject image in repo["images"])
    {
        if (image["manifest"] is not null)
        {
            string digest = image["manifest"]["digest"].ToString().Replace("mcr.microsoft.com", "dotnetdocker.azurecr.io/public");
            filteredDigests.Add(digest);
        }

        foreach (JObject platform in image["platforms"])
        {
            string digest = platform["digest"].ToString().Replace("mcr.microsoft.com", "dotnetdocker.azurecr.io/public");
            filteredDigests.Add(digest);
        }
    }

    return digests.Where(digest => (digest.ProductVersion is not null && eolDates.ContainsKey(digest.ProductVersion.ToString(2))) || !filteredDigests.Contains(digest.Digest));
}

DateOnly? GetEolDate(Version version)
{
    if (eolDates.TryGetValue(version.ToString(2), out DateOnly eolDate))
    {
        return eolDate;
    }

    return null;
}

async Task<IEnumerable<DigestInfo>> GetNonKustoDataAsync(string repoName, Dictionary<string, DigestInfo> kustoRows, bool checkDotNetVersion)
{
    string queryRepoName = $"public/{repoName}";
    const string Registry = "dotnetdocker.azurecr.io";
    Uri registryUri = new($"https://{Registry}");
    ContainerRegistryClient client = new(registryUri, new DefaultAzureCredential());
    var repo = client.GetRepository(queryRepoName);
    var props = repo.GetAllManifestProperties();

    ConcurrentBag<DigestInfo> nonKustoDigests = new();

    Regex versionRegex = new(@"^(?<version>\d+\.\d+)");
    ContainerRegistryContentClient contentClient = new(registryUri, queryRepoName, new DefaultAzureCredential());

    await Parallel.ForEachAsync(props, async (prop, cts) =>
    {
        string digest = $"{repoName}@{prop.Digest}";
        if (!kustoRows.ContainsKey(digest))
        {
            Azure.Response<GetManifestResult> manifest;
            try
            {
                manifest = await contentClient.GetManifestAsync(prop.Digest);
            }
            catch(Exception)
            {
                failedDigests.Add(digest);
                return;
            }
            
            var manifestObj = manifest.Value.Manifest.ToObjectFromJson<JsonObject>();

            if (manifestObj["subject"] is not null)
            {
                return;
            }
            bool hasVersionTag = false;
            if (checkDotNetVersion)
            {
                var versionTag = prop.Tags.FirstOrDefault(tag => versionRegex.IsMatch(tag));
                if (versionTag is not null)
                {
                    hasVersionTag = true;
                    var productVersion = new Version(versionRegex.Match(versionTag).Groups["version"].Value);

                    DateOnly? eolDate = GetEolDate(productVersion);
                    if (eolDate is not null)
                    {
                        nonKustoDigests.Add(new DigestInfo($"{Registry}/public/{digest}", productVersion, eolDate.Value, null));
                    }
                }
            }
            
            if (!hasVersionTag)
            {
                var config = manifestObj["config"];
                DateTimeOffset created;
                if (config is not null)
                {
                    string? configDigest = manifestObj["config"]?["digest"]?.ToString();
                    DownloadRegistryBlobResult configBlob = await contentClient.DownloadBlobContentAsync(configDigest);
                    var configJson = configBlob.Content.ToObjectFromJson<JsonObject>();
                    created = DateTimeOffset.Parse(configJson["created"].ToString());
                }
                else
                {
                    created = prop.CreatedOn;
                }
                
                if (created < DateTime.UtcNow.AddMonths(-1))
                {
                    var eolDate = DateOnly.FromDateTime(created.AddMonths(1).UtcDateTime);
                    nonKustoDigests.Add(new DigestInfo($"{Registry}/public/{digest}", null, eolDate, Dockerfile: null));
                }
            }
        }
    });

    return nonKustoDigests;

    //
    //ConcurrentBag<string> imageDigests = new();
    //ConcurrentBag<string> annotatedDigests = new();
    //await Parallel.ForEachAsync(props, async (prop, cts) =>
    //{

    //    var manifest = await contentClient.GetManifestAsync(prop.Digest);
    //    var manifestObj = manifest.Value.Manifest.ToObjectFromJson<JsonObject>();
    //    string? annotatedDigest = manifestObj["subject"]?["digest"]?.ToString();
    //    if (annotatedDigest is not null)
    //    {
    //        annotatedDigests.Add(annotatedDigest);
    //    }
    //    else
    //    {
    //        imageDigests.Add(prop.Digest.Substring(prop.Digest.IndexOf('@') + 1));
    //    }
    //});

    //var notAnnotated = imageDigests.Except(annotatedDigests).ToList();
}

static async Task<IEnumerable<DigestInfo>> FilterNonMarDigestsAsync(IEnumerable<DigestInfo> values)
{
    RegistryClient client = new("mcr.microsoft.com");

    ConcurrentBag<DigestInfo> results = new();

    await Parallel.ForEachAsync(values, async (item, cts) =>
    {
        string repo = item.Digest.Substring(0, item.Digest.IndexOf('@'));
        string digest = item.Digest.Substring(item.Digest.IndexOf('@') + 1);

        bool exists = await client.Manifests.ExistsAsync(repo, digest);

        if (!exists)
        {
            Console.WriteLine($"Digest {digest} for {repo} does not exist in the registry");
            return;
        }

        string repoDigest = $"dotnetdocker.azurecr.io/public/{repo}@{digest}";

        results.Add(new (repoDigest, item.ProductVersion, item.EolDate, item.Dockerfile));
    });

    return results;
}

Dictionary<string, DigestInfo> GetKustoData(string repoName)
{
    Dictionary<string, DigestInfo> digests = new();
    const string clusterResource = "https://Dotnettel.kusto.windows.net";
    KustoConnectionStringBuilder connectionBuilder = new KustoConnectionStringBuilder(clusterResource)
        .WithAadAzureTokenCredentialsAuthentication(new DefaultAzureCredential());
    using var kustoClient = KustoClientFactory.CreateCslQueryProvider(connectionBuilder);

    string query = $"""
    DotNetDockerImageInfo
    | where Image startswith "sha256"
    | where ProductVersion != ""
    | where Repository == "{repoName}"
    | project Digest = strcat(Repository, "@", Image), ProductVersion, Dockerfile
    | distinct Digest, ProductVersion, Dockerfile
    | order by ProductVersion asc, Digest
    """;

    var response = kustoClient.ExecuteQuery("Telemetry", query, null);
    while (response.Read())
    {
        string name = response.GetString(0);
        Version? productVersion = null;
        Version.TryParse(response.GetString(1), out productVersion);
        string dockerfile = response.GetString(2);

        if (productVersion is null)
        {
            continue;
        }
        DateOnly? eolDate = GetEolDate(productVersion);
        if (eolDate is not null)
        {
            if (digests.TryGetValue(name, out DigestInfo? existing))
            {
                if (existing.EolDate > eolDate.Value)
                {
                    existing.EolDate = eolDate.Value;
                }
            }
            else
            {
                digests.Add(name, new DigestInfo(name, productVersion, eolDate.Value, dockerfile));
            }
        }
    }

    return digests;
}

internal record DigestInfo(string Digest, Version? ProductVersion, DateOnly EolDate, string? Dockerfile)
{
    public DateOnly EolDate { get; set; } = EolDate;
}

public class EolDigestData
{
    public string Digest { get; set; }

    public DateOnly? EolDate { get; set; }

    public List<string> Tags { get; set; } = new();
}

public class EolAnnotationsData
{
    public EolAnnotationsData()
    {
    }

    public EolAnnotationsData(List<EolDigestData> eolDigests, DateOnly? eolDate = null)
    {
        EolDate = eolDate;
        EolDigests = eolDigests;
    }

    public DateOnly? EolDate { get; set; }

    public List<EolDigestData> EolDigests { get; set; } = [];
}
