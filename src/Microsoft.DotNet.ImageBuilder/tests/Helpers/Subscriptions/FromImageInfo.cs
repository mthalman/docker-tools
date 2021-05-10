// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions
{
    public class FromImageInfo
    {
        public FromImageInfo(string name, string digest)
        {
            Name = name;
            Digest = digest;
        }

        public string Digest { get; }
        public string Name { get; }
    }
}
