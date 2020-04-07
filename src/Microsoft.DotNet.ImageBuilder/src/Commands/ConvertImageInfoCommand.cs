using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using V1 = Microsoft.DotNet.ImageBuilder.Models.ImageV1;
using V2 = Microsoft.DotNet.ImageBuilder.Models.Image;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    [Export(typeof(ICommand))]
    public class ConvertImageInfoCommand : ManifestCommand<ConvertImageInfoOptions>
    {
        private readonly IDockerService dockerService;
        private readonly IManifestToolService manifestToolService;

        [ImportingConstructor]
        public ConvertImageInfoCommand(IDockerService dockerService, IManifestToolService manifestToolService)
        {
            this.dockerService = dockerService;
            this.manifestToolService = manifestToolService ?? throw new ArgumentNullException(nameof(manifestToolService));
        }

        public override Task ExecuteAsync()
        {
            string imageInfoText = File.ReadAllText(Options.ImageInfoPath);

            V2.ImageArtifactDetails imageArtifactDetails;
            if (Options.CreatedDate)
            {
                imageArtifactDetails = ImageInfoHelper.LoadFromContent(imageInfoText, Manifest);
                GetCreatedDates(imageArtifactDetails);
            }
            else
            {
                V1.RepoData[] repos = JsonConvert.DeserializeObject<V1.RepoData[]>(imageInfoText);
                imageArtifactDetails = GenerateDefault(repos);
            }

            string outputDir = Path.GetDirectoryName(Options.ImageInfoOutputPath);
            string filenameBase = Path.GetFileNameWithoutExtension(Options.ImageInfoOutputPath);

            string output = JsonHelper.SerializeObject(imageArtifactDetails);
            File.WriteAllText(Options.ImageInfoOutputPath, output);

            imageArtifactDetails = ImageInfoHelper.LoadFromContent(output, Manifest, true);
            RemoveOutOfDateContent(imageArtifactDetails);

            return Task.CompletedTask;
        }

        private void GetCreatedDates(V2.ImageArtifactDetails imageArtifactDetails)
        {
            foreach (var repo in imageArtifactDetails.Repos)
            {
                foreach (var image in repo.Images.Where(image => Manifest.GetFilteredImages().Contains(image.ManifestImage)))
                {
                    DateTime mostRecentSimpleTag = DateTime.MinValue.ToUniversalTime();

                    foreach (var platform in image.Platforms)
                    {
                        var platformInfo = image.ManifestImage.FilteredPlatforms.FirstOrDefault(p => platform.Equals(p));
                        if (platformInfo != null)
                        {
                            string tag = TagInfo.GetFullyQualifiedName("mcr.microsoft.com/" + repo.Repo, platform.SimpleTags.First());
                            this.dockerService.PullImage(tag, false);
                            platform.Created = this.dockerService.GetCreatedDate(tag, false).ToUniversalTime();
                            if (platform.Created > mostRecentSimpleTag)
                            {
                                mostRecentSimpleTag = platform.Created;
                            }
                        }
                    }

                    if (image.Manifest != null)
                    {
                        image.Manifest.Created = mostRecentSimpleTag;
                    }
                }

            }
        }

        private V2.ImageArtifactDetails GenerateDefault(V1.RepoData[] repos)
        {
            V2.ImageArtifactDetails imageArtifactDetails = new V2.ImageArtifactDetails();

            foreach (var repo in repos)
            {
                RepoInfo manifestRepo = Manifest.AllRepos
                    .FirstOrDefault(manifestRepo => manifestRepo.Model.Name == repo.Repo);

                if (manifestRepo is null)
                {
                    continue;
                }

                V2.RepoData newRepo = new V2.RepoData
                {
                    Repo = repo.Repo
                };

                imageArtifactDetails.Repos.Add(newRepo);

                foreach (var manifestImage in manifestRepo.AllImages)
                {
                    // Find all images
                    var images = repo.Images
                        .Where(image =>
                            manifestImage.AllPlatforms
                                .Any(platform => image.Key == GetPlatformDockerfile(platform)));
                    if (images.Any())
                    {
                        V2.ManifestData manifestData = null;

                        if (manifestImage.SharedTags?.Any() == true)
                        {
                            JArray tagManifest = this.manifestToolService.Inspect(manifestImage.SharedTags.First().FullyQualifiedName, false);
                            string digest = tagManifest
                                .OfType<JObject>()
                                .First(manifestType => manifestType["MediaType"].Value<string>() == PublishManifestCommand.ManifestListMediaType)
                                ["Digest"].Value<string>();

                            manifestData = new V2.ManifestData
                            {
                                SharedTags = manifestImage.SharedTags
                                    .Select(tag => tag.Name)
                                      .ToList(),
                                Digest = digest
                            };
                        }

                        V2.ImageData newImage = new V2.ImageData
                        {
                            ProductVersion = manifestImage.Model.ProductVersion,
                            Manifest = manifestData,
                            Platforms = images
                                .Select(image =>
                                {
                                    var manifestPlatform = manifestImage.AllPlatforms.First(p => GetPlatformDockerfile(p) == image.Key);

                                    string baseImageDigest;
                                    if (image.Key.Contains("nano"))
                                    {
                                        baseImageDigest = image.Value.BaseImages?.FirstOrDefault(baseImage => baseImage.Key.Contains("nano")).Value;
                                    }
                                    else
                                    {
                                        baseImageDigest = image.Value.BaseImages?.First().Value;
                                    }

                                    return new V2.PlatformData
                                    {
                                        Dockerfile = image.Key,
                                        Architecture = manifestPlatform.Model.Architecture.GetDisplayName(),
                                        BaseImageDigest = baseImageDigest,
                                        Digest = image.Value.Digest,
                                        OsType = manifestPlatform.Model.OS.ToString(),
                                        SimpleTags = image.Value.SimpleTags,
                                        OsVersion = manifestPlatform.GetOSDisplayName()
                                    };
                                })
                                .ToList()
                        };

                        newRepo.Images.Add(newImage);
                    }
                }
            }

            return imageArtifactDetails;
        }

        private static string GetPlatformDockerfile(PlatformInfo platform)
        {
            return platform.DockerfilePathRelativeToManifest.EndsWith("/Dockerfile") ? platform.DockerfilePathRelativeToManifest : platform.DockerfilePathRelativeToManifest + "/Dockerfile";
        }

        private void RemoveOutOfDateContent(V2.ImageArtifactDetails imageArtifactDetails)
        {
            for (int repoIndex = imageArtifactDetails.Repos.Count - 1; repoIndex >= 0; repoIndex--)
            {
                V2.RepoData repoData = imageArtifactDetails.Repos[repoIndex];
                RepoInfo manifestRepo = Manifest.AllRepos.FirstOrDefault(manifestRepo => manifestRepo.Name == "mcr.microsoft.com/" + repoData.Repo);

                // If there doesn't exist a matching repo in the manifest, remove it from the image info
                if (manifestRepo is null)
                {
                    imageArtifactDetails.Repos.Remove(repoData);
                    continue;
                }

                for (int imageIndex = repoData.Images.Count - 1; imageIndex >= 0; imageIndex--)
                {
                    V2.ImageData imageData = repoData.Images[imageIndex];
                    ImageInfo manifestImage = imageData.ManifestImage;

                    // If there doesn't exist a matching image in the manifest, remove it from the image info
                    if (manifestImage is null)
                    {
                        repoData.Images.Remove(imageData);
                        continue;
                    }

                    for (int platformIndex = imageData.Platforms.Count - 1; platformIndex >= 0; platformIndex--)
                    {
                        V2.PlatformData platformData = imageData.Platforms[platformIndex];
                        PlatformInfo manifestPlatform = manifestImage.AllPlatforms
                            .FirstOrDefault(manifestPlatform => platformData.Equals(manifestPlatform));

                        // If there doesn't exist a matching platform in the manifest, remove it from the image info
                        if (manifestPlatform is null)
                        {
                            imageData.Platforms.Remove(platformData);
                        }
                    }
                }
            }
        }
    }
}
