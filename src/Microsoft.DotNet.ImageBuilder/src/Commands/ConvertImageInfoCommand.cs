using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Newtonsoft.Json;

using V1 = Microsoft.DotNet.ImageBuilder.Models.ImageV1;
using V2 = Microsoft.DotNet.ImageBuilder.Models.Image;

namespace Microsoft.DotNet.ImageBuilder.Commands
{
    [Export(typeof(ICommand))]
    public class ConvertImageInfoCommand : ManifestCommand<ConvertImageInfoOptions>
    {
        private readonly IDockerService dockerService;

        [ImportingConstructor]
        public ConvertImageInfoCommand(IDockerService dockerService)
        {
            this.dockerService = dockerService;
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

            return Task.CompletedTask;
        }

        private void GetCreatedDates(V2.ImageArtifactDetails imageArtifactDetails)
        {
            foreach (var image in imageArtifactDetails.Repos.SelectMany(repo => repo.Images).Where(image => Manifest.GetFilteredImages().Contains(image.ManifestImage)))
            {
                DateTime mostRecentSimpleTag = DateTime.MinValue.ToUniversalTime();
                if (image.SharedTags != null)
                {
                    mostRecentSimpleTag = image.SharedTags.First().Created;
                }

                foreach (var platform in image.Platforms)
                {
                    var platformInfo = image.ManifestImage.FilteredPlatforms.FirstOrDefault(p => platform.Equals(p));
                    if (platformInfo != null)
                    {
                        this.dockerService.PullImage(platformInfo.Tags.First().FullyQualifiedName, false);
                        platform.Created = this.dockerService.GetCreatedDate(platformInfo.Tags.First().FullyQualifiedName, false).ToUniversalTime();
                        if (platform.Created > mostRecentSimpleTag)
                        {
                            mostRecentSimpleTag = platform.Created;
                        }
                    }
                }

                if (image.SharedTags != null)
                {
                    image.SharedTags.ForEach(tag => tag.Created = mostRecentSimpleTag);
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
                        var sharedTags = manifestImage.SharedTags?
                            .Select(tag => new V2.SharedTag
                            {
                                Name = tag.Name
                            })
                            .ToList();
                        if (sharedTags?.Any() == false)
                        {
                            sharedTags = null;
                        }

                        V2.ImageData newImage = new V2.ImageData
                        {
                            ProductVersion = manifestImage.Model.ProductVersion,
                            SharedTags = sharedTags,
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
    }
}
