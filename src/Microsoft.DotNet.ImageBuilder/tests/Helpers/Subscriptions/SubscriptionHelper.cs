// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Moq;
using System.IO;
using Newtonsoft.Json;
using System.IO.Compression;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;

namespace Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions
{
    public static class SubscriptionHelper
    {
        /// <summary>
        /// Returns an <see cref="IHttpClientProvider"/> that creates an <see cref="HttpClient"/> which 
        /// bypasses the network and return back pre-built responses for GitHub repo zip files.
        /// </summary>
        /// <param name="subscriptionInfos">Mapping of data to subscriptions.</param>
        /// <param name="dockerfileInfos">A mapping of Git repos to their associated set of Dockerfiles.</param>
        /// <param name="createFolderPath">Delegate to create the path to store the generated files.</param>
        public static IHttpClientProvider CreateHttpClientFactory(
            SubscriptionInfo[] subscriptionInfos,
            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos,
            Func<string> createFolderPath)
        {
            Dictionary<string, HttpResponseMessage> responses = new();
            foreach (SubscriptionInfo subscriptionInfo in subscriptionInfos)
            {
                Subscription subscription = subscriptionInfo.Subscription;
                List<DockerfileInfo> repoDockerfileInfos = dockerfileInfos[subscription.Manifest];
                string repoZipPath = GenerateRepoZipFile(subscription, subscriptionInfo.Manifest, repoDockerfileInfos, createFolderPath);

                responses.Add(
                    $"https://github.com/{subscription.Manifest.Owner}/{subscription.Manifest.Repo}/archive/{subscription.Manifest.Branch}.zip",
                    new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new ByteArrayContent(File.ReadAllBytes(repoZipPath))
                    });
            }

            HttpClient client = new(new TestHttpMessageHandler(responses));

            Mock<IHttpClientProvider> httpClientFactoryMock = new();
            httpClientFactoryMock
                .Setup(o => o.GetClient())
                .Returns(client);

            return httpClientFactoryMock.Object;
        }

        /// <summary>
        /// Generates a zip file in a temp location that represents the contents of a GitHub repo.
        /// </summary>
        /// <param name="subscription">The subscription associated with the GitHub repo.</param>
        /// <param name="manifest">Manifest model associated with the subscription.</param>
        /// <param name="repoDockerfileInfos">Set of <see cref="DockerfileInfo"/> objects that describe the Dockerfiles contained in the repo.</param>
        /// <param name="createFolderPath">Delegate to create the path to store the generated files.</param>
        private static string GenerateRepoZipFile(
            Subscription subscription,
            Manifest manifest,
            List<DockerfileInfo> repoDockerfileInfos,
            Func<string> createFolderPath)
        {
            string path = createFolderPath();

            string repoPath = Directory.CreateDirectory(
                Path.Combine(path, $"{subscription.Manifest.Repo}-{subscription.Manifest.Branch}")).FullName;

            // Serialize the manifest model to a file in the repo folder.
            string manifestPath = Path.Combine(repoPath, subscription.Manifest.Path);
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest));

            foreach (DockerfileInfo dockerfileInfo in repoDockerfileInfos)
            {
                GenerateDockerfile(dockerfileInfo, repoPath);
            }

            string repoZipPath = Path.Combine(path, "repo.zip");
            ZipFile.CreateFromDirectory(repoPath, repoZipPath, CompressionLevel.Fastest, true);
            return repoZipPath;
        }

        /// <summary>
        /// Generates a Dockerfile from the <see cref="DockerfileInfo"/> metatada and stores it the specified path.
        /// </summary>
        /// <param name="dockerfileInfo">The metadata for the Dockerfile.</param>
        /// <param name="destinationPath">Folder path to store the Dockerfile.</param>
        private static void GenerateDockerfile(DockerfileInfo dockerfileInfo, string destinationPath)
        {
            string dockerfileContents = string.Empty;

            foreach (FromImageInfo fromImage in dockerfileInfo.FromImages)
            {
                dockerfileContents += $"FROM {fromImage.Name}{Environment.NewLine}";
            }

            string dockerfilePath = Directory.CreateDirectory(
                Path.Combine(destinationPath, Path.GetDirectoryName(dockerfileInfo.DockerfilePath))).FullName;

            File.WriteAllText(
                Path.Combine(dockerfilePath, Path.GetFileName(dockerfileInfo.DockerfilePath)), dockerfileContents);
        }
    }
}
