// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;

namespace Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions
{
    public class SubscriptionInfo
    {
        public SubscriptionInfo(Subscription subscription, Manifest manifest, ImageArtifactDetails imageInfo = null)
        {
            Subscription = subscription;
            Manifest = manifest;
            ImageInfo = imageInfo;
        }

        public Subscription Subscription { get; }
        public Manifest Manifest { get; }
        public ImageArtifactDetails ImageInfo { get; }
    }
}
