// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions
{
    public class DockerfileInfo
    {
        public DockerfileInfo(string dockerfilePath, params FromImageInfo[] fromImages)
        {
            DockerfilePath = dockerfilePath;
            FromImages = fromImages;
        }

        public string DockerfilePath { get; }
        public FromImageInfo[] FromImages { get; }
    }
}
