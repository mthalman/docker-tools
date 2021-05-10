// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using static Microsoft.DotNet.ImageBuilder.Commands.CliHelper;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class QueueBuildOptions : Options
    {
        public string SubscriptionsPath { get; set; } = string.Empty;
        public AzdoOptions AzdoOptions { get; set; } = new();
        public IEnumerable<string> AllSubscriptionImagePaths { get; set; } = Enumerable.Empty<string>();
        public GitOptions GitOptions { get; set; } = new();
        public bool EnableUpgradablePackages { get; set; }
        public string SourceUrl { get; set; } = string.Empty;
    }

    public class QueueBuildOptionsBuilder : CliOptionsBuilder
    {
        private readonly AzdoOptionsBuilder _azdoOptionsBuilder = new();
        private readonly GitOptionsBuilder _gitOptionsBuilder =
            GitOptionsBuilder.Build()
                .WithAuthToken(description: "Auth token to use to connect to GitHub for posting notifications")
                .WithOwner(description: "Owner of the GitHub repo to post notifications to")
                .WithRepo(description: "Name of the GitHub repo to post notifications to");

        private const string DefaultSubscriptionsPath = "subscriptions.json";
        public override IEnumerable<Option> GetCliOptions() =>
            base.GetCliOptions()
                .Concat(_azdoOptionsBuilder.GetCliOptions())
                .Concat(
                    new Option[]
                    {
                        CreateOption("subscriptions-path", nameof(QueueBuildOptions.SubscriptionsPath),
                            $"Path to the subscriptions file", DefaultSubscriptionsPath),
                        CreateMultiOption<string>("image-paths", nameof(QueueBuildOptions.AllSubscriptionImagePaths),
                            "JSON string mapping a subscription ID to the image paths to be built (from the output variable of getStaleImages)"),
                        CreateOption("enable-upgradable-pkgs", nameof(QueueBuildOptions.EnableUpgradablePackages),
                            "Allows a build to be queued for a Dockerfile path whose only rebuild reason is an upgradable package", false),
                    }
                .Concat(_gitOptionsBuilder.GetCliOptions()));

        public override IEnumerable<Argument> GetCliArguments() =>
            base.GetCliArguments()
                .Concat(_azdoOptionsBuilder.GetCliArguments())
                .Concat(_gitOptionsBuilder.GetCliArguments())
                .Concat(new Argument[]
                {
                    new Argument(nameof(QueueBuildOptions.SourceUrl))
                    {
                        Description = "URL of the build that is executing the queue command"
                    }
                });
    }
}
#nullable disable
