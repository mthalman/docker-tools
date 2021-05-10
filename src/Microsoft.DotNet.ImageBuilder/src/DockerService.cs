﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder
{
    [Export(typeof(IDockerService))]
    internal class DockerService : IDockerService
    {
        private readonly IManifestToolService _manifestToolService;

        public Architecture Architecture => DockerHelper.Architecture;

        [ImportingConstructor]
        public DockerService(IManifestToolService manifestToolService)
        {
            _manifestToolService = manifestToolService ?? throw new ArgumentNullException(nameof(manifestToolService));
        }

        public string? GetImageDigest(string image, bool isDryRun)
        {
            IEnumerable<string> digests = DockerHelper.GetImageDigests(image, isDryRun);

            // A digest will not exist for images that have been built locally or have been manually installed
            if (!digests.Any())
            {
                return null;
            }

            string digestSha = _manifestToolService.GetManifestDigestSha(ManifestMediaType.Any, image, isDryRun);

            if (digestSha is null)
            {
                return null;
            }

            string digest = DockerHelper.GetDigestString(DockerHelper.GetRepo(image), digestSha);

            if (!digests.Contains(digest))
            {
                throw new InvalidOperationException(
                    $"Found published digest '{digestSha}' for tag '{image}' but could not find a matching digest value from " +
                    $"the set of locally pulled digests for this tag: { string.Join(", ", digests) }. This most likely means that " +
                    "this tag has been updated since it was last pulled.");
            }

            return digest;
        }

        public IEnumerable<string> GetImageManifestLayers(string image, bool isDryRun) => _manifestToolService.GetImageLayers(image, isDryRun);

        public void PullImage(string image, bool isDryRun) => DockerHelper.PullImage(image, isDryRun);

        public void PushImage(string tag, bool isDryRun) => ExecuteHelper.ExecuteWithRetry("docker", $"push {tag}", isDryRun);

        public void CreateTag(string image, string tag, bool isDryRun) => DockerHelper.CreateTag(image, tag, isDryRun);

        public string BuildImage(
            string dockerfilePath,
            string buildContextPath,
            IEnumerable<string> tags,
            IDictionary<string, string> buildArgs,
            bool isRetryEnabled,
            bool isDryRun)
        {
            string tagArgs = $"-t {string.Join(" -t ", tags)}";

            IEnumerable<string> buildArgList = buildArgs
                .Select(buildArg => $" --build-arg {buildArg.Key}={buildArg.Value}");
            string buildArgsString = string.Join(string.Empty, buildArgList);

            string dockerArgs = $"build {tagArgs} -f {dockerfilePath}{buildArgsString} {buildContextPath}";

            if (isRetryEnabled)
            {
                return ExecuteHelper.ExecuteWithRetry("docker", dockerArgs, isDryRun);
            }
            else
            {
                return ExecuteHelper.Execute("docker", dockerArgs, isDryRun);
            }
        }

        public string Run(string image, string command, string? name = null, bool skipAutoCleanup = false, string? entrypoint = null,
            IDictionary<string, string>? volumeMounts = null, bool isDryRun = false)
        {
            List<string> options = new()
            {
                name is null ? string.Empty : $"--name {name}",
                skipAutoCleanup ? string.Empty : "--rm",
                volumeMounts is not null ?
                    string.Concat(volumeMounts.Select(kvp => $"-v {kvp.Key}:{kvp.Value}")) :
                    string.Empty,
                entrypoint is not null ? $"--entrypoint {entrypoint}" : string.Empty
            };

            string optionsStr = string.Join(" ", options);

            return ExecuteHelper.Execute("docker", $"run {optionsStr} {image} {command}", isDryRun);
        }

        public void Copy(string src, string dst, bool isDryRun) => ExecuteHelper.Execute("docker", $"cp {src} {dst}", isDryRun);

        public void DeleteContainer(string containerName, bool isDryRun)
        {
            if (ContainerExists(containerName, isDryRun))
{
                ExecuteHelper.Execute("docker", $"rm -f {containerName}", isDryRun);
            }
        }

        public bool LocalImageExists(string tag, bool isDryRun) => DockerHelper.LocalImageExists(tag, isDryRun);

        public long GetImageSize(string image, bool isDryRun) => DockerHelper.GetImageSize(image, isDryRun);

        public DateTime GetCreatedDate(string image, bool isDryRun)
        {
            if (isDryRun)
            {
                return default;
            }

            return DateTime.Parse(DockerHelper.GetCreatedDate(image, isDryRun));
        }

        private static bool ContainerExists(string containerName, bool isDryRun) =>
            ResourceExists("container", $"-f \"name={containerName}\"", isDryRun);

        private static bool ResourceExists(string type, string filterArg, bool isDryRun)
        {
            string output = ExecuteHelper.Execute("docker", $"{type} ls -a -q {filterArg}", isDryRun);
            return !string.IsNullOrEmpty(output);
        }
    }
}
#nullable disable
