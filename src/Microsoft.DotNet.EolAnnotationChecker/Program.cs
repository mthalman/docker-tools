using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Containers.ContainerRegistry;
using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;

const string LifecycleArtifactType = "application/vnd.microsoft.artifact.lifecycle";
Dictionary<string, DateOnly> eolDates = new()
{
    { "7.0", new DateOnly(2024, 5, 14) },
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
rootCmd.AddArgument(eolDataPathArg);
rootCmd.AddArgument(repoArg);
rootCmd.SetHandler(
    Execute,
    eolDataPathArg,
    repoArg);

return rootCmd.Invoke(args);

void Execute(string outputPath, string repo)
{
    ExecuteAsync(outputPath, repo).Wait();
}

async Task ExecuteAsync(string outputPath, string repo)
{
    Dictionary<string, DigestInfo> kustoDigests = GetKustoData(repo);

    var nonKustoDigests = await GetNonKustoDataAsync(repo, kustoDigests);

    bool combine = true;

    IEnumerable<DigestInfo> digests;

    if (combine)
    {
        digests = kustoDigests.Select(val => val.Value);
        digests = await FilterNonMarDigestsAsync(digests);
        digests = digests.Union(nonKustoDigests);
    }
    else
    {
        digests = nonKustoDigests;
    }

    digests = FilterOutAnnotatedDigests(digests);

    digests = digests.OrderBy(row => row.Digest);

    EolAnnotationsData eolAnnotationsData = new();
    
    foreach (var digestInfo in digests.OrderBy(row => row.Digest))
    {
        eolAnnotationsData.EolDigests.Add(new EolDigestData { Digest = digestInfo.Digest, EolDate = digestInfo.EolDate, Tags = [digestInfo.Dockerfile ?? digestInfo.ProductVersion?.ToString()] });
    }

    string json = System.Text.Json.JsonSerializer.Serialize(eolAnnotationsData, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outputPath, json);
}

static IEnumerable<DigestInfo> FilterOutAnnotatedDigests(IEnumerable<DigestInfo> digests)
{
    ConcurrentBag<DigestInfo> newDigests = [];
    Parallel.ForEach(digests, (digest, _) =>
    {
        if (!IsAnnotated(digest.Digest))
        {
            newDigests.Add(digest);
        }
    });

    return newDigests;
}

static bool IsAnnotated(string digest)
{
    ProcessStartInfo processInfo = new("oras", $"discover --artifact-type {LifecycleArtifactType} --format json {digest}");
    processInfo.RedirectStandardOutput = true;
    Process process = new()
    {
        EnableRaisingEvents = true,
        StartInfo = processInfo
    };

    DataReceivedEventHandler getDataReceivedHandler(StringBuilder stringBuilder, TextWriter outputWriter)
    {
        return new DataReceivedEventHandler((sender, e) =>
        {
            string? line = e.Data;
            if (line != null)
            {
                stringBuilder.AppendLine(line);
                outputWriter.WriteLine(line);
            }
        });
    }

    StringBuilder stdOutput = new();
    process.OutputDataReceived += getDataReceivedHandler(stdOutput, Console.Out);

    process.Start();
    process.BeginOutputReadLine();
    process.WaitForExit();

    string output = stdOutput.ToString();

    return LifecycleAnnotationExists(output);
}

static bool LifecycleAnnotationExists(string json)
{
    OrasDiscoverData? orasDiscoverData = JsonConvert.DeserializeObject<OrasDiscoverData>(json);
    if (orasDiscoverData?.Manifests != null)
    {
        return orasDiscoverData.Manifests.Where(m => m.ArtifactType == LifecycleArtifactType).Any();
    }
    return false;
}

DateOnly? GetEolDate(Version version)
{
    if (eolDates.TryGetValue(version.ToString(2), out DateOnly eolDate))
    {
        return eolDate;
    }

    return null;
}

async Task<IEnumerable<DigestInfo>> GetNonKustoDataAsync(string repoName, Dictionary<string, DigestInfo> kustoRows)
{
    string queryRepoName = $"public/{repoName}";
    Uri registryUri = new("https://dotnetdocker.azurecr.io");
    ContainerRegistryClient client = new(registryUri, new DefaultAzureCredential());
    var repo = client.GetRepository(queryRepoName);
    var props = repo.GetAllManifestProperties();

    ConcurrentBag<DigestInfo> nonKustoDigests = new();

    Regex versionRegex = new(@"^(?<version>\d+\.\d+)");
    ContainerRegistryContentClient contentClient = new(registryUri, queryRepoName, new DefaultAzureCredential());

    await Parallel.ForEachAsync(props, async (prop, cts) =>
    {
        string digest = $"{repoName}@{prop.Digest}";
        if (!kustoRows.ContainsKey(digest) && await IsImageDigestAsync(contentClient, prop.Digest))
        {
            var versionTag = prop.Tags.FirstOrDefault(tag => versionRegex.IsMatch(tag));
            if (versionTag is not null)
            {
                var productVersion = new Version(versionRegex.Match(versionTag).Groups["version"].Value);

                DateOnly? eolDate = GetEolDate(productVersion);
                if (eolDate is not null)
                {
                    nonKustoDigests.Add(new DigestInfo(digest, productVersion, eolDate.Value, null));
                }
            }
            // A dangling image might be supported. Check if it's older than a month.
            else if (prop.CreatedOn < DateTime.UtcNow.AddMonths(-1))
            {
                var eolDate = DateOnly.FromDateTime(prop.CreatedOn.AddMonths(1).UtcDateTime);
                nonKustoDigests.Add(new DigestInfo(digest, null, eolDate, Dockerfile: null));
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

static async Task<bool> IsImageDigestAsync(ContainerRegistryContentClient contentClient, string digest)
{
    var manifest = await contentClient.GetManifestAsync(digest);
    var manifestObj = manifest.Value.Manifest.ToObjectFromJson<JsonObject>();
    return manifestObj["subject"] is null;
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
        .WithAadAzCliAuthentication();
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

public class OrasDiscoverData
{
    public List<OciManifest>? Manifests { get; set; }

    public OrasDiscoverData()
    {
    }
}

public class OciManifest
{
    public string? ArtifactType { get; set; }

    public OciManifest()
    {
    }
}
