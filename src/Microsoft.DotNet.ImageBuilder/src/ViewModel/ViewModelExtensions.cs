// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.ImageBuilder.ViewModel
{
    public static class ViewModelExtensions
    {
        public static IEnumerable<PlatformInfo> GetDependencyGraph(
            this PlatformInfo parent, IEnumerable<PlatformInfo> allPlatforms)
        {
            string[] parentTags = parent.Tags.Select(tag => tag.FullyQualifiedName).ToArray();
            IEnumerable<PlatformInfo> dependencies = allPlatforms
                .Where(platform => platform.InternalFromImages.Intersect(parentTags).Any());
            return new PlatformInfo[] {parent}
                .Concat(dependencies.SelectMany(child => child.GetDependencyGraph(allPlatforms)));
        }

        public static IEnumerable<PlatformInfo> GetAncestors(
            this PlatformInfo platform, IEnumerable<PlatformInfo> allPlatforms)
        {
            IEnumerable<PlatformInfo> parents = allPlatforms
                .Where(platform => platform.Tags.Any(tag => platform.InternalFromImages.Contains(tag.FullyQualifiedName)));

            foreach (PlatformInfo parent in parents)
            {
                yield return parent;
                foreach (PlatformInfo ancestor in parent.GetAncestors(allPlatforms))
                {
                    yield return ancestor;
                }
            }
        }
    }
}
