// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Microsoft.DotNet.VersionTools.Automation;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;
using Newtonsoft.Json;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    [Export(typeof(ICommand))]
    public class GetStaleImagesCommand : Command<GetStaleImagesOptions, GetStaleImagesOptionsBuilder>, IDisposable
    {
        private const string DefaultShellEntrypoint = "/bin/sh";

        private readonly Dictionary<string, string> _imageDigests = new();
        private readonly object _imageDigestsLock = new();
        private readonly IManifestToolService _manifestToolService;
        private readonly ILoggerService _loggerService;
        private readonly IGitHubClientFactory _gitHubClientFactory;
        private readonly IDockerService _dockerService;
        private readonly HttpClient _httpClient;

        [ImportingConstructor]
        public GetStaleImagesCommand(
            IManifestToolService manifestToolService,
            IHttpClientProvider httpClientProvider,
            ILoggerService loggerService,
            IGitHubClientFactory gitHubClientFactory,
            IDockerService dockerService)
        {
            _manifestToolService = manifestToolService;
            _loggerService = loggerService;
            _gitHubClientFactory = gitHubClientFactory;
            _dockerService = dockerService;
            _httpClient = httpClientProvider.GetClient();
        }

        protected override string Description => "Gets paths to images whose base images are out-of-date";

        public override async Task ExecuteAsync()
        {
            string subscriptionsJson = File.ReadAllText(Options.SubscriptionOptions.SubscriptionsPath);
            Subscription[] subscriptions = JsonConvert.DeserializeObject<Subscription[]>(subscriptionsJson);

            IEnumerable<(Subscription Subscription, ManifestInfo Manifest)> subscriptionManifests =
                await SubscriptionHelper.GetSubscriptionManifestsAsync(
                    Options.SubscriptionOptions.SubscriptionsPath, Options.FilterOptions, _httpClient);

            List<SubscriptionRebuildInfo> rebuildInfos = new();

            foreach ((Subscription Subscription, ManifestInfo Manifest) subscriptionManifest in subscriptionManifests)
            {
                IList<ImageRebuildInfo> imageRebuildInfos =
                    await GetImageRebuildInfosAsync(subscriptionManifest.Subscription, subscriptionManifest.Manifest);

                if (imageRebuildInfos.Any())
                {
                    rebuildInfos.Add(new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionManifest.Subscription.Id,
                        ImageRebuildInfos = imageRebuildInfos
                    });
                }
            }

            string outputString = JsonConvert.SerializeObject(rebuildInfos);

            _loggerService.WriteMessage(
                PipelineHelper.FormatOutputVariable(Options.VariableName, outputString)
                    .Replace("\"", "\\\"")); // Escape all quotes

            string formattedResults = JsonConvert.SerializeObject(rebuildInfos, Formatting.Indented);
            _loggerService.WriteMessage(
                $"Image Paths to be Rebuilt:{Environment.NewLine}{formattedResults}");
        }

        private async Task<IList<ImageRebuildInfo>> GetImageRebuildInfosAsync(Subscription subscription, ManifestInfo manifest)
        {
            ImageArtifactDetails imageArtifactDetails = await GetImageInfoForSubscriptionAsync(subscription, manifest);

            List<ImageRebuildInfo> imageRebuildInfos = new();

            IEnumerable<PlatformInfo> allPlatforms = manifest.GetAllPlatforms().ToList();

            foreach (RepoInfo repo in manifest.FilteredRepos)
            {
                IEnumerable<PlatformInfo> platforms = repo.FilteredImages
                    .SelectMany(image => image.FilteredPlatforms);

                RepoData? repoData = imageArtifactDetails.Repos
                    .FirstOrDefault(s => s.Repo == repo.Name);

                foreach (PlatformInfo platform in platforms)
                {
                    ImageRebuildInfo.MergeImageRebuildInfos(GetImageRebuildInfos(allPlatforms, platform, repoData), imageRebuildInfos);
                }
            }

            return imageRebuildInfos;
        }

        private IList<ImageRebuildInfo> GetImageRebuildInfos(
            IEnumerable<PlatformInfo> allPlatforms, PlatformInfo platform, RepoData? repoData)
        {
            bool foundImageInfo = false;

            List<ImageRebuildInfo> imageRebuildInfos = new();

            void processPlatformWithMissingImageInfo(PlatformInfo platform)
            {
                _loggerService.WriteMessage(
                    $"WARNING: Image info not found for '{platform.DockerfilePath}'. Adding path to build to be queued anyway.");
                IEnumerable<PlatformInfo> dependentPlatforms = platform.GetDependencyGraph(allPlatforms);
                foreach (PlatformInfo platformInfo in dependentPlatforms)
                {
                    ImageRebuildInfo.MergeImageRebuildInfo(
                        new ImageRebuildInfo
                        {
                            DockerfilePath = platformInfo.Model.Dockerfile,
                            Reasons = new List<ImageRebuildReason>
                            {
                                new ImageRebuildReason
                                {
                                    ReasonType = ImageRebuildReasonType.MissingImageInfo,
                                    MarkdownMessage = "Missing image info data"
                                }
                            }
                        },
                        imageRebuildInfos);
                }
            }

            if (repoData == null || repoData.Images == null)
            {
                processPlatformWithMissingImageInfo(platform);
                return imageRebuildInfos;
            }

            foreach (ImageData imageData in repoData.Images)
            {
                PlatformData? platformData = imageData.Platforms
                    .FirstOrDefault(platformData => platformData.PlatformInfo == platform);
                if (platformData is not null)
                {
                    foundImageInfo = true;
                    string fromImage = platform.FinalStageFromImage;
                    string currentDigest = GetCurrentDigest(fromImage);

                    ImageRebuildInfo? imageRebuildInfo = null;
                    if (platformData.BaseImageDigest != currentDigest)
                    {
                        imageRebuildInfo = new ImageRebuildInfo
                        {
                            DockerfilePath = platformData.PlatformInfo.Model.Dockerfile,
                            Reasons = new List<ImageRebuildReason>
                            {
                                new ImageRebuildReason
                                {
                                    ReasonType = ImageRebuildReasonType.BaseImageChange,
                                    MarkdownMessage = GetDigestDifferenceMarkdown(platformData.BaseImageDigest, currentDigest)
                                }
                            }
                        };
                    }

                    IEnumerable<UpgradablePackage> upgradablePackages = Enumerable.Empty<UpgradablePackage>();

                    if (imageRebuildInfo is null && platformData.PlatformInfo.OS == OS.Linux)
                    {
                        upgradablePackages = GetUpgradablePackages(platformData);

                        if (upgradablePackages.Any())
                        {
                            imageRebuildInfo = new ImageRebuildInfo
                            {
                                DockerfilePath = platformData.PlatformInfo.Model.Dockerfile,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.UpgradablePackage,
                                        MarkdownMessage = GetUpgradablePackagesMarkdown(upgradablePackages)
                                    }
                                }
                            };
                        }
                    }

                    LogImageStatus(platform, platformData, fromImage, currentDigest, imageRebuildInfo, upgradablePackages);

                    if (imageRebuildInfo is not null)
                    {
                        ImageRebuildInfo.MergeImageRebuildInfo(imageRebuildInfo, imageRebuildInfos);
                    }

                    break;
                }
            }

            if (!foundImageInfo)
            {
                processPlatformWithMissingImageInfo(platform);
            }

            return imageRebuildInfos;
        }

        private static string GetDigestDifferenceMarkdown(string baseImageDigest, string currentDigest) =>
            "Digest difference:" +
            "<ul>" +
                $"<li>Old: `{GetShortenedSha(baseImageDigest)}`</li>" +
                $"<li>New: `{GetShortenedSha(currentDigest)}`</li>" +
            "</ul>";

        private static string GetUpgradablePackagesMarkdown(IEnumerable<UpgradablePackage> upgradablePackages) =>
            "Upgradable packages:" +
            "<ul>" +
                string.Concat(upgradablePackages
                    .Select(pkg =>
                        $"<li>`{pkg.Name}`" +
                            "<ul>" +
                                $"<li>Old: `{pkg.CurrentVersion}`</li>" +
                                $"<li>New: `{pkg.UpgradeVersion}`</li>" +
                            "</ul>" +
                        "</li>")) +
            "</ul>";


        private static string GetShortenedSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
            {
                return sha;
            }

            string[] shaParts = sha.Split(':');

            const int MaxDigestLength = 7;
            if (shaParts[1].Length > MaxDigestLength)
            {
                return shaParts[0] + shaParts[1].Substring(MaxDigestLength) + "...";
            }
            else
            {
                return sha;
            }
        }

        private void LogImageStatus(
            PlatformInfo platform, PlatformData platformData, string fromImage, string currentDigest,
            ImageRebuildInfo? imageRebuildInfo, IEnumerable<UpgradablePackage> upgradablePackages)
        {
            StringBuilder builder = new();
            builder.AppendLine($"Rebuild check report for '{platform.DockerfilePath}'");
            builder.AppendLine($"\tBase image:           {fromImage}");
            builder.AppendLine($"\tPrevious base digest: {platformData.BaseImageDigest}");
            builder.AppendLine($"\tCurrent base digest:  {currentDigest}");
            builder.AppendLine($"\tUpgradable packages:  {(upgradablePackages.Any() ? string.Join(" ", upgradablePackages) : "<none>")}");
            builder.AppendLine($"\tImage is up-to-date:  {imageRebuildInfo is null}");
            _loggerService.WriteMessage(builder.ToString());
        }

        private IEnumerable<UpgradablePackage> GetUpgradablePackages(PlatformData platformData)
        {
            string[] installedPackages = GetInstalledPackages(platformData.Digest);
            string[] baseInstalledPackages = GetInstalledPackages(platformData.BaseImageDigest);
            IEnumerable<string> targetPackages = installedPackages.Except(baseInstalledPackages);

            const string containerScriptsDir = "/.scripts";

            Dictionary<string, string> volumeMounts = new()
            {
                { Path.GetDirectoryName(Options.GetUpgradablePackagesScriptPath) ?? "/", containerScriptsDir }
            };

            string targetPackagesArgs = string.Join(" ", targetPackages);
            string scriptPath = $"{containerScriptsDir}/{Path.GetFileName(Options.GetUpgradablePackagesScriptPath)}";
            string resultsFile = $"results-{DateTime.Now.ToFileTime()}.txt";
            string containerResultsPath = $"/{resultsFile}";
            string localResultsPath = Path.Combine(Path.GetTempPath(), resultsFile);
            string command = $"{scriptPath} {containerResultsPath} {targetPackagesArgs}";
            string containerName = $"pkgContainer-{DateTime.Now.ToFileTime()}";

            try
            {
                _dockerService.Run(
                    platformData.Digest,
                    command,
                    containerName,
                    skipAutoCleanup: true,
                    DefaultShellEntrypoint,
                    volumeMounts,
                    Options.IsDryRun);
                
                _dockerService.Copy($"{containerName}:{containerResultsPath}", localResultsPath, Options.IsDryRun);

                return File.ReadAllText(localResultsPath)
                    .Trim()
                    .Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line =>
                    {
                        string[] parts = line.Split(',');
                        return new UpgradablePackage(name: parts[0], currentVersion: parts[1], upgradeVersion: parts[2]);
                    })
                    .ToList();
            }
            finally
            {
                _dockerService.DeleteContainer(containerName, Options.IsDryRun);
                File.Delete(localResultsPath);
            }
        }

        private string[] GetInstalledPackages(string image)
        {
            _dockerService.PullImage(image, Options.IsDryRun);

            const string containerScriptsDir = "/.scripts";

            Dictionary<string, string> volumeMounts = new()
            {
                { Path.GetDirectoryName(Options.GetInstalledPackagesScriptPath) ?? "/", containerScriptsDir }
            };

            string scriptPath = $"{containerScriptsDir}/{Path.GetFileName(Options.GetInstalledPackagesScriptPath)}";
            string output = _dockerService.Run(image, scriptPath, entrypoint: DefaultShellEntrypoint, volumeMounts: volumeMounts, isDryRun: Options.IsDryRun);
            return output.Split(Environment.NewLine);
        }

        private string GetCurrentDigest(string fromImage) =>
            LockHelper.DoubleCheckedLockLookup(_imageDigestsLock, _imageDigests, fromImage,
                () =>
                {
                    string digest = _manifestToolService.GetManifestDigestSha(ManifestMediaType.Any, fromImage, Options.IsDryRun);
                    return DockerHelper.GetDigestString(DockerHelper.GetRepo(fromImage), digest);
                });

        private async Task<ImageArtifactDetails> GetImageInfoForSubscriptionAsync(Subscription subscription, ManifestInfo manifest)
        {
            string imageDataJson;
            using (IGitHubClient gitHubClient = _gitHubClientFactory.GetClient(Options.GitOptions.ToGitHubAuth(), Options.IsDryRun))
            {
                GitHubProject project = new(subscription.ImageInfo.Repo, subscription.ImageInfo.Owner);
                GitHubBranch branch = new(subscription.ImageInfo.Branch, project);

                GitFile repo = subscription.Manifest;
                imageDataJson = await gitHubClient.GetGitHubFileContentsAsync(subscription.ImageInfo.Path, branch);
            }

            return ImageInfoHelper.LoadFromContent(imageDataJson, manifest, skipManifestValidation: true);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private class UpgradablePackage
        {
            public UpgradablePackage(string name, string currentVersion, string upgradeVersion)
            {
                Name = name;
                CurrentVersion = currentVersion;
                UpgradeVersion = upgradeVersion;
            }

            public string Name { get; }
            public string CurrentVersion { get; }
            public string UpgradeVersion { get; }

            public override string ToString() => $"{Name} ({CurrentVersion} => {UpgradeVersion})";
        }
    }
}
#nullable disable
