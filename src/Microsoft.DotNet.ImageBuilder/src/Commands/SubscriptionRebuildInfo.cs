// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class SubscriptionRebuildInfo
    {
        public string SubscriptionId { get; set; } = "";

        public IList<ImageRebuildInfo> ImageRebuildInfos { get; set; } = new List<ImageRebuildInfo>();
    }

    public class ImageRebuildInfo
    {
        public string DockerfilePath { get; set; } = "";
        public IList<ImageRebuildReason> Reasons { get; set; } = new List<ImageRebuildReason>();

        public static IList<ImageRebuildInfo> MergeImageRebuildInfos(IList<ImageRebuildInfo> src, IList<ImageRebuildInfo> dest)
        {
            foreach (ImageRebuildInfo info in src)
            {
                MergeImageRebuildInfo(info, dest);
            }

            return dest;
        }

        public static void MergeImageRebuildInfo(ImageRebuildInfo rebuildInfo, IList<ImageRebuildInfo> dest)
        {
            ImageRebuildInfo? dstRebuildInfo = dest.FirstOrDefault(info => info.DockerfilePath == rebuildInfo.DockerfilePath);

            if (dstRebuildInfo is not null)
            {
                foreach (ImageRebuildReason srcReason in rebuildInfo.Reasons)
                {
                    if (!dstRebuildInfo.Reasons.Any(dstReason => dstReason.ReasonType == srcReason.ReasonType))
                    {
                        dstRebuildInfo.Reasons.Add(srcReason);
                    }
                }
            }
            else
            {
                dest.Add(rebuildInfo);
            }
        }
    }

    public class ImageRebuildReason
    {
        public ImageRebuildReasonType ReasonType { get; set; }
        public string MarkdownMessage { get; set; } = "";
    }

    public enum ImageRebuildReasonType
    {
        BaseImageChange,
        DependentImageChange,
        MissingImageInfo,
        UpgradablePackage
    }
}
#nullable disable
