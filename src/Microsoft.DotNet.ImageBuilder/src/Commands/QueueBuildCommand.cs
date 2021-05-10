// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Microsoft.DotNet.ImageBuilder.Services;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using WebApi = Microsoft.TeamFoundation.Build.WebApi;

#nullable enable
namespace Microsoft.DotNet.ImageBuilder.Commands
{
    [Export(typeof(ICommand))]
    public class QueueBuildCommand : Command<QueueBuildOptions, QueueBuildOptionsBuilder>
    {
        private readonly IVssConnectionFactory _connectionFactory;
        private readonly ILoggerService _loggerService;
        private readonly INotificationService _notificationService;
        private readonly HttpClient _httpClient;

        // The number of most recent builds that must have failed consecutively before skipping the queuing of another build
        public const int BuildFailureLimit = 3;

        [ImportingConstructor]
        public QueueBuildCommand(
            IVssConnectionFactory connectionFactory,
            ILoggerService loggerService,
            INotificationService notificationService,
            IHttpClientProvider httpClientProvider)
        {
            _connectionFactory = connectionFactory;
            _loggerService = loggerService;
            _notificationService = notificationService;
            _httpClient = httpClientProvider.GetClient();
        }

        protected override string Description => "Queues builds to update images";

        public override async Task ExecuteAsync()
        {
            ManifestFilterOptions filterOptions = new()
            {
                Architecture = "*",
                OsType = "*"
            };

            IEnumerable<(Subscription Subscription, ManifestInfo Manifest)> subscriptionManifests =
                await SubscriptionHelper.GetSubscriptionManifestsAsync(
                    Options.SubscriptionsPath, filterOptions, _httpClient);

            IEnumerable<SubscriptionRebuildInfo> imagePathsBySubscription = GetSubscriptionRebuildInfos();

            if (imagePathsBySubscription.Any())
            {
                await Task.WhenAll(
                    imagePathsBySubscription.Select(
                        async kvp =>
                        {
                            (Subscription Subscription, ManifestInfo Manifest) subscriptionManifest =
                                subscriptionManifests.First(sub => sub.Subscription.Id == kvp.SubscriptionId);
                            await QueueBuildForStaleImagesAsync(
                                subscriptionManifest.Subscription, subscriptionManifest.Manifest, kvp.ImageRebuildInfos);
                        }));
            }
            else
            {
                _loggerService.WriteMessage(
                    $"None of the subscriptions have base images that are out-of-date. No rebuild necessary.");
            }
        }

        private IEnumerable<SubscriptionRebuildInfo> GetSubscriptionRebuildInfos()
        {
            // This data comes from the GetStaleImagesCommand and represents a mapping of a subscription to the Dockerfile paths
            // of the images that need to be built. A given subscription may have images that are spread across Linux/Windows, AMD64/ARM
            // which means that the paths collected for that subscription were spread across multiple jobs.  Each of the items in the 
            // Enumerable here represents the data collected by one job.  We need to consolidate the paths for a given subscription since
            // they could be spread across multiple items in the Enumerable.
            return Options.AllSubscriptionImagePaths
                .SelectMany(allImagePaths => JsonConvert.DeserializeObject<SubscriptionRebuildInfo[]>(allImagePaths))
                .GroupBy(imagePaths => imagePaths.SubscriptionId)
                .Select(group => new SubscriptionRebuildInfo
                {
                    SubscriptionId = group.Key,
                    ImageRebuildInfos = group
                        .Aggregate(new SubscriptionRebuildInfo { SubscriptionId = group.Key }, (current, next) =>
                            new SubscriptionRebuildInfo
                            {
                                SubscriptionId = group.Key,
                                ImageRebuildInfos = ImageRebuildInfo.MergeImageRebuildInfos(next.ImageRebuildInfos, current.ImageRebuildInfos)
                            })
                        .ImageRebuildInfos
                });
        }

        private void SplitImageRebuildInfos(
            IList<ImageRebuildInfo> imageRebuildInfos,
            out IEnumerable<ImageRebuildInfo> actionableRebuildInfos,
            out IEnumerable<NonActionableRebuildInfo> nonActionableRebuildInfos)
        {
            List<ImageRebuildInfo> localActionableRebuildInfos = new();
            List<NonActionableRebuildInfo> localNonActionableRebuildInfos = new();

            foreach (ImageRebuildInfo rebuildInfo in imageRebuildInfos)
            {
                if (!Options.EnableUpgradablePackages &&
                    rebuildInfo.Reasons.Count == 1 &&
                    rebuildInfo.Reasons[0].ReasonType == ImageRebuildReasonType.UpgradablePackage)
                {
                    localNonActionableRebuildInfos.Add(
                        new NonActionableRebuildInfo("This Dockerfile was targeted for a rebuild only due to the presence of an upgradable " +
                            "package but queuing builds for upgradable packages is not enabled.")
                        {
                            DockerfilePath = rebuildInfo.DockerfilePath,
                            Reasons = rebuildInfo.Reasons
                        });
                }
                else
                {
                    localActionableRebuildInfos.Add(rebuildInfo);
                }
            }

            actionableRebuildInfos = localActionableRebuildInfos;
            nonActionableRebuildInfos = localNonActionableRebuildInfos;
        }

        private async Task QueueBuildForStaleImagesAsync(Subscription subscription, ManifestInfo manifest, IList<ImageRebuildInfo> imageRebuildInfos)
        {
            if (!imageRebuildInfos.Any())
            {
                _loggerService.WriteMessage(
                    $"All images for subscription '{subscription}' are using up-to-date base images. No rebuild necessary.");
                return;
            }

            SplitImageRebuildInfos(imageRebuildInfos,
                out IEnumerable<ImageRebuildInfo> actionableRebuildInfos,
                out IEnumerable<NonActionableRebuildInfo> nonActionableRebuildInfos);

            if (!actionableRebuildInfos.Any())
            {
                await LogAndNotifyResultsAsync(
                    subscription, actionableRebuildInfos, nonActionableRebuildInfos, queuedBuild: null, exception: null,
                    inProgressBuilds: null, recentFailedBuilds: null);
                return;
            }

            List<ImageRebuildInfo> expandedActionableRebuildInfos = ExpandActionableRebuildInfos(manifest, actionableRebuildInfos);

            string formattedPathsToRebuild = expandedActionableRebuildInfos
                .Select(rebuildInfo => $"{CliHelper.FormatAlias(ManifestFilterOptionsBuilder.PathOptionName)} '{rebuildInfo.DockerfilePath}'")
                .Aggregate((p1, p2) => $"{p1} {p2}");

            string parameters = "{\"" + subscription.PipelineTrigger.PathVariable + "\": \"" + formattedPathsToRebuild + "\"}";

            _loggerService.WriteMessage($"Queueing build for subscription {subscription} with parameters {parameters}.");

            if (Options.IsDryRun)
            {
                return;
            }

            WebApi.Build? queuedBuild = null;
            Exception? exception = null;
            IEnumerable<string>? inProgressBuilds = null;
            IEnumerable<string>? recentFailedBuilds = null;

            try
            {
                (Uri baseUrl, VssCredentials credentials) = Options.AzdoOptions.GetConnectionDetails();

                using (IVssConnection connection = _connectionFactory.Create(baseUrl, credentials))
                using (IProjectHttpClient projectHttpClient = connection.GetProjectHttpClient())
                using (IBuildHttpClient client = connection.GetBuildHttpClient())
                {
                    TeamProject project = await projectHttpClient.GetProjectAsync(Options.AzdoOptions.Project);

                    WebApi.Build build = new()
                    {
                        Project = new TeamProjectReference { Id = project.Id },
                        Definition = new WebApi.BuildDefinitionReference { Id = subscription.PipelineTrigger.Id },
                        SourceBranch = subscription.Manifest.Branch,
                        Parameters = parameters
                    };
                    build.Tags.Add(AzdoTags.AutoBuilder);

                    inProgressBuilds = await GetInProgressBuildsAsync(client, subscription.PipelineTrigger.Id, project.Id);
                    if (!inProgressBuilds.Any())
                    {
                        (bool shouldDisallowBuild, IEnumerable<string> recentFailedBuildsLocal) =
                            await ShouldDisallowBuildDueToRecentFailuresAsync(client, subscription.PipelineTrigger.Id, project.Id);
                        recentFailedBuilds = recentFailedBuildsLocal;
                        if (shouldDisallowBuild)
                        {
                            _loggerService.WriteMessage(
                                PipelineHelper.FormatErrorCommand("Unable to queue build due to too many recent build failures."));
                        }
                        else
                        {
                            queuedBuild = await client.QueueBuildAsync(build);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                await LogAndNotifyResultsAsync(
                    subscription,
                    expandedActionableRebuildInfos,
                    nonActionableRebuildInfos,
                    queuedBuild,
                    exception,
                    inProgressBuilds,
                    recentFailedBuilds);
            }
        }

        /// <summary>
        /// Expands the set of actionable rebuild infos to include the dependency graph of each of the Dockerfiles.
        /// </summary>
        private static List<ImageRebuildInfo> ExpandActionableRebuildInfos(ManifestInfo manifest, IEnumerable<ImageRebuildInfo> actionableRebuildInfos)
        {
            List<ImageRebuildInfo> expandedActionableRebuildInfos = new(actionableRebuildInfos);

            IEnumerable<PlatformInfo> allPlatforms = manifest.GetAllPlatforms();
            foreach (ImageRebuildInfo imageRebuildInfo in actionableRebuildInfos)
            {
                IEnumerable<PlatformInfo> platforms = manifest.GetPlatformsByDockerfile(imageRebuildInfo.DockerfilePath);
                foreach (PlatformInfo platform in platforms)
                {
                    IEnumerable<PlatformInfo> ancestors = platform.GetAncestors(allPlatforms);
                    MergeRelatedPlatforms(expandedActionableRebuildInfos, platform, ancestors, new ImageRebuildReason
                    {
                        ReasonType = ImageRebuildReasonType.DependentImageChange,
                        MarkdownMessage = $"Ancestor of Dockerfile '{platform.Model.Dockerfile}'"
                    });

                    IEnumerable<PlatformInfo> dependentPlatforms = platform.GetDependencyGraph(allPlatforms)
                        .Except(new PlatformInfo[] { platform });
                    MergeRelatedPlatforms(expandedActionableRebuildInfos, platform, dependentPlatforms, new ImageRebuildReason
                    {
                        ReasonType = ImageRebuildReasonType.BaseImageChange,
                        MarkdownMessage = $"Dependency on Dockerfile '{platform.Model.Dockerfile}'"
                    });
                }
            }

            return expandedActionableRebuildInfos;
        }

        private static void MergeRelatedPlatforms(List<ImageRebuildInfo> expandedActionableRebuildInfos, PlatformInfo platform,
            IEnumerable<PlatformInfo> relatedPlatforms, ImageRebuildReason reason)
        {
            foreach (PlatformInfo ancestor in relatedPlatforms)
            {
                ImageRebuildInfo.MergeImageRebuildInfo(
                    new ImageRebuildInfo
                    {
                        DockerfilePath = ancestor.Model.Dockerfile,
                        Reasons = new List<ImageRebuildReason>
                        {
                            reason
                        }
                    }, expandedActionableRebuildInfos);
            }
        }

        private async Task LogAndNotifyResultsAsync(
            Subscription subscription,
            IEnumerable<ImageRebuildInfo> actionableRebuildInfos,
            IEnumerable<NonActionableRebuildInfo> nonActionableRebuildInfos,
            WebApi.Build? queuedBuild,
            Exception? exception,
            IEnumerable<string>? inProgressBuilds,
            IEnumerable<string>? recentFailedBuilds)
        {
            StringBuilder notificationMarkdown = new();

            notificationMarkdown.AppendLine($"Subscription: {subscription}");
            notificationMarkdown.AppendLine($"Queued by: {Options.SourceUrl}");
            notificationMarkdown.AppendLine();
            WriteTargetDockerfilesMarkdown(actionableRebuildInfos, notificationMarkdown);
            WriteSkippedDockerfilesMarkdown(nonActionableRebuildInfos, notificationMarkdown);

            notificationMarkdown.AppendLine("## Details");
            notificationMarkdown.AppendLine();

            string? category = null;
            if (queuedBuild is not null)
            {
                category = LogAndNotifyQueuedBuild(queuedBuild, notificationMarkdown);
            }
            else if (recentFailedBuilds is not null)
            {
                category = LogAndNotifyRecentlyFailedBuilds(subscription, recentFailedBuilds, notificationMarkdown);
            }
            else if (!actionableRebuildInfos.Any())
            {
                category = LogAndNotifyNoActionableDockerfiles(subscription, notificationMarkdown);
            }
            else if (inProgressBuilds is not null)
            {
                category = LogAndNotifyInProgressBuilds(subscription, inProgressBuilds, notificationMarkdown);
            }
            else if (exception != null)
            {
                category = LogAndNotifyFailure(exception, notificationMarkdown);
            }
            else
            {
                throw new NotSupportedException("Unknown state");
            }

            string header = $"AutoBuilder - {category}";
            notificationMarkdown.Insert(0, $"# {header}{Environment.NewLine}{Environment.NewLine}");

            try
            {
                if (Options.GitOptions.AuthToken == string.Empty ||
                    Options.GitOptions.Owner == string.Empty ||
                    Options.GitOptions.Repo == string.Empty)
                {
                    _loggerService.WriteMessage(
                        "Skipping posting of notification because GitHub auth token, owner, and repo options were not provided.");
                }
                else
                {
                    await _notificationService.PostAsync($"{header} - {subscription}", notificationMarkdown.ToString(),
                        new string[] { NotificationLabels.AutoBuilder }.AppendIf(NotificationLabels.Failure, () => exception is not null),
                        $"https://github.com/{Options.GitOptions.Owner}/{Options.GitOptions.Repo}", Options.GitOptions.AuthToken);
                }
            }
            finally
            {
                _loggerService.WriteMessage(
                    $"[BEGIN NOTIFICATION MARKDOWN]{Environment.NewLine}{notificationMarkdown}{Environment.NewLine}[END NOTIFICATION MARKDOWN]");
            }
        }

        private static string LogAndNotifyFailure(Exception exception, StringBuilder notificationMarkdown)
        {
            string? category = "Failed";
            notificationMarkdown.AppendLine("An exception was thrown when attempting to queue the build:");
            notificationMarkdown.AppendLine();
            notificationMarkdown.AppendLine("```");
            notificationMarkdown.AppendLine(exception.ToString());
            notificationMarkdown.AppendLine("```");
            return category;
        }

        private string LogAndNotifyInProgressBuilds(Subscription subscription, IEnumerable<string> inProgressBuilds, StringBuilder notificationMarkdown)
        {
            string? category = "Skipped";
            StringBuilder builder = new();
            builder.AppendLine($"The following in-progress builds were detected on the pipeline for subscription '{subscription}':");
            foreach (string buildUri in inProgressBuilds)
            {
                builder.AppendLine(buildUri);
            }

            builder.AppendLine();
            builder.AppendLine("Queueing the build will be skipped.");

            string message = builder.ToString();

            _loggerService.WriteMessage(message);
            notificationMarkdown.AppendLine(message);
            return category;
        }

        private string LogAndNotifyNoActionableDockerfiles(Subscription subscription, StringBuilder notificationMarkdown)
        {
            string? category = "Skipped";
            string msg = $"There are no actionable rebuilds available for subscription '{subscription}'. No build will be queued.";
            _loggerService.WriteMessage(msg);
            notificationMarkdown.AppendLine(msg);
            return category;
        }

        private string LogAndNotifyRecentlyFailedBuilds(Subscription subscription, IEnumerable<string> recentFailedBuilds, StringBuilder notificationMarkdown)
        {
            string? category = "Failed";
            StringBuilder builder = new();
            builder.AppendLine(
                $"Due to recent failures of the following builds, a build will not be queued again for subscription '{subscription}':");
            builder.AppendLine();
            foreach (string buildUri in recentFailedBuilds)
            {
                builder.AppendLine($"* {buildUri}");
            }

            builder.AppendLine();
            builder.AppendLine(
                "Please investigate the cause of the failures, resolve the issue, and manually queue a build for the Dockerfile paths listed above.");

            string message = builder.ToString();

            _loggerService.WriteMessage(message);
            notificationMarkdown.AppendLine(message);
            return category;
        }

        private string LogAndNotifyQueuedBuild(WebApi.Build queuedBuild, StringBuilder notificationMarkdown)
        {
            string? category = "Queued";
            string webLink = GetWebLink(queuedBuild);
            _loggerService.WriteMessage($"Queued build {webLink}");
            notificationMarkdown.AppendLine($"[Link to queued build]({webLink})");
            return category;
        }

        private static void WriteSkippedDockerfilesMarkdown(IEnumerable<NonActionableRebuildInfo> nonActionableRebuildInfos, StringBuilder notificationMarkdown)
        {
            if (!nonActionableRebuildInfos.Any())
            {
                return;
            }

            IEnumerable<IGrouping<string, NonActionableRebuildInfo>> nonActionableRebuildInfosGroupedByReason =
                nonActionableRebuildInfos
                    .GroupBy(info => info.NonActionableReason);

            notificationMarkdown.AppendLine("## Skipped Dockerfiles");
            notificationMarkdown.AppendLine();
            notificationMarkdown.AppendLine("Rebuilds of the following Dockerfile paths were not executed.");
            notificationMarkdown.AppendLine();

            foreach (IGrouping<string, NonActionableRebuildInfo> rebuildInfoGroup in nonActionableRebuildInfosGroupedByReason)
            {
                notificationMarkdown.AppendLine($"Reason: {rebuildInfoGroup.Key}");
                notificationMarkdown.AppendLine();
                WriteImageRebuildMarkdownTable(rebuildInfoGroup, notificationMarkdown);
                notificationMarkdown.AppendLine();
            }
        }

        private static void WriteTargetDockerfilesMarkdown(IEnumerable<ImageRebuildInfo> actionableRebuildInfos, StringBuilder notificationMarkdown)
        {
            if (!actionableRebuildInfos.Any())
            {
                return;
            }

            notificationMarkdown.AppendLine("## Target Dockerfiles");
            notificationMarkdown.AppendLine();
            WriteImageRebuildMarkdownTable(actionableRebuildInfos, notificationMarkdown);
            notificationMarkdown.AppendLine();
        }

        private static void WriteImageRebuildMarkdownTable(IEnumerable<ImageRebuildInfo> imageRebuildInfos, StringBuilder notificationMarkdown)
        {
            notificationMarkdown.AppendLine("Path | Details");
            notificationMarkdown.AppendLine("--- | --- ");

            foreach (ImageRebuildInfo rebuildInfo in imageRebuildInfos)
            {
                string reasons = string.Join("<br/>",
                    rebuildInfo.Reasons
                        .Select(reason => reason.MarkdownMessage)
                        .ToArray());
                notificationMarkdown.AppendLine($"{rebuildInfo.DockerfilePath} | {reasons}");
            }
        }

        private static async Task<IEnumerable<string>> GetInProgressBuildsAsync(IBuildHttpClient client, int pipelineId, Guid projectId)
        {
            IPagedList<WebApi.Build> builds = await client.GetBuildsAsync(
                projectId, definitions: new int[] { pipelineId }, statusFilter: WebApi.BuildStatus.InProgress);
            return builds.Select(build => GetWebLink(build));
        }

        private static string GetWebLink(WebApi.Build build) =>
            ((ReferenceLink)build.Links.Links["web"]).Href;

		private static async Task<(bool ShouldSkipBuild, IEnumerable<string> RecentFailedBuilds)> ShouldDisallowBuildDueToRecentFailuresAsync(
            IBuildHttpClient client, int pipelineId, Guid projectId)
        {
            List<WebApi.Build> autoBuilderBuilds = (await client.GetBuildsAsync(projectId, definitions: new int[] { pipelineId }))
                .Where(build => build.Tags.Contains(AzdoTags.AutoBuilder))
                .OrderByDescending(build => build.QueueTime)
                .Take(BuildFailureLimit)
                .ToList();

            if (autoBuilderBuilds.Count == BuildFailureLimit &&
                autoBuilderBuilds.All(build => build.Status == WebApi.BuildStatus.Completed && build.Result == WebApi.BuildResult.Failed))
            {
                return (true, autoBuilderBuilds.Select(GetWebLink));
            }

            return (false, Enumerable.Empty<string>());
        }

        private class NonActionableRebuildInfo : ImageRebuildInfo
        {
            public NonActionableRebuildInfo(string nonActionableReason)
            {
                NonActionableReason = nonActionableReason;
            }

            public string NonActionableReason { get; }
        }
    }
}
#nullable disable
