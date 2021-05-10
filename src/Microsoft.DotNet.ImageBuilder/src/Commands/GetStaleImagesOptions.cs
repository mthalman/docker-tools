// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.CommandLine;
using System.Linq;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    public class GetStaleImagesOptions : Options, IFilterableOptions, IGitOptionsHost
    {
        public ManifestFilterOptions FilterOptions { get; set; } = new();

        public GitOptions GitOptions { get; set; } = new();

        public SubscriptionOptions SubscriptionOptions { get; set; } = new();

        public string VariableName { get; set; } = string.Empty;

        public string GetInstalledPackagesScriptPath { get; set; } = string.Empty;

        public string GetUpgradablePackagesScriptPath { get; set; } = string.Empty;
    }

    public class GetStaleImagesOptionsBuilder : CliOptionsBuilder
    {
        private readonly GitOptionsBuilder _gitOptionsBuilder = GitOptionsBuilder.BuildWithDefaults();
        private readonly ManifestFilterOptionsBuilder _manifestFilterOptionsBuilder = new();
        private readonly SubscriptionOptionsBuilder _subscriptionOptionsBuilder = new();

        public override IEnumerable<Option> GetCliOptions() =>
            base.GetCliOptions()
                .Concat(_subscriptionOptionsBuilder.GetCliOptions())
                .Concat(_manifestFilterOptionsBuilder.GetCliOptions())
                .Concat(_gitOptionsBuilder.GetCliOptions());

        public override IEnumerable<Argument> GetCliArguments() =>
            base.GetCliArguments()
                .Concat(_subscriptionOptionsBuilder.GetCliArguments())
                .Concat(_manifestFilterOptionsBuilder.GetCliArguments())
                .Concat(_gitOptionsBuilder.GetCliArguments())
                .Concat(
                    new Argument[]
                    {
                        new Argument<string>(nameof(GetStaleImagesOptions.VariableName),
                            "The Azure Pipeline variable name to assign the image paths to"),
                        new Argument<string>(nameof(GetStaleImagesOptions.GetInstalledPackagesScriptPath),
                            "Path to the script file that outputs list of installed packages"),
                        new Argument<string>(nameof(GetStaleImagesOptions.GetUpgradablePackagesScriptPath),
                            "Path to the script file that outputs list of upgradable packages")
                    });
    }
}
#nullable disable
