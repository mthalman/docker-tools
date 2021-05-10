// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Commands;
using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Microsoft.DotNet.ImageBuilder.Services;
using Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using static Microsoft.DotNet.ImageBuilder.Tests.Helpers.ManifestHelper;
using WebApi = Microsoft.TeamFoundation.Build.WebApi;

namespace Microsoft.DotNet.ImageBuilder.Tests
{
    public class QueueBuildCommandTests
    {
        /// <summary>
        /// Verifies that no build is queued if a build is currently in progress.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_BuildInProgress()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            PagedList<WebApi.Build> inProgressBuilds = new()
            {
                CreateBuild("http://contoso")
            };

            using (TestContext context = new(
                allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos, inProgressBuilds, new PagedList<WebApi.Build>()))
            {
                await context.ExecuteCommandAsync();

                // Normally this state would cause a build to be queued but since
                // a build is marked as in progress, it doesn't.

                context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: false);
            }
        }

        /// <summary>
        /// Verifies that no build is queued if there are too many recent failed builds.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_RecentFailedBuilds_MaxFailed()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            PagedList<WebApi.Build> allBuilds = new();

            for (int i = 0; i < QueueBuildCommand.BuildFailureLimit; i++)
            {
                WebApi.Build failedBuild = CreateBuild($"https://failedbuild-{i}");
                failedBuild.Tags.Add(AzdoTags.AutoBuilder);
                failedBuild.Status = WebApi.BuildStatus.Completed;
                failedBuild.Result = WebApi.BuildResult.Failed;
                allBuilds.Add(failedBuild);
            }

            using TestContext context = new(
                allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos, new PagedList<WebApi.Build>(), allBuilds);
            await context.ExecuteCommandAsync();

            context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: false);
        }

        /// <summary>
        /// Verifies that a build is queued even if there are recent failed builds but not enough to meet the threshold.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_RecentFailedBuilds_PartialFailed()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            PagedList<WebApi.Build> allBuilds = new();

            for (int i = 0; i < QueueBuildCommand.BuildFailureLimit - 1; i++)
            {
                WebApi.Build failedBuild = CreateBuild($"https://failedbuild-{i}");
                failedBuild.Tags.Add(AzdoTags.AutoBuilder);
                failedBuild.Status = WebApi.BuildStatus.Completed;
                failedBuild.Result = WebApi.BuildResult.Failed;
                allBuilds.Add(failedBuild);
            }

            using TestContext context = new(
                allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos, new PagedList<WebApi.Build>(), allBuilds);
            await context.ExecuteCommandAsync();


            Dictionary<Subscription, IList<string>> expectedPathsBySubscription = new()
            {
                {
                    subscriptionInfos[0].Subscription,
                    new List<string>
                    {
                        dockerfile1Path
                    }
                }
            };

            context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: true, expectedPathsBySubscription);
        }

        /// <summary>
        /// Verifies that a build is queued when the set of recent builds consist of some recently succeeded builds.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_RecentFailedBuilds_SucceededAndFailed()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            PagedList<WebApi.Build> allBuilds = new();

            WebApi.Build succeeded = CreateBuild($"https://succeededbuild");
            succeeded.Tags.Add(AzdoTags.AutoBuilder);
            succeeded.Status = WebApi.BuildStatus.Completed;
            succeeded.Result = WebApi.BuildResult.Succeeded;
            allBuilds.Add(succeeded);

            for (int i = 0; i < QueueBuildCommand.BuildFailureLimit; i++)
            {
                WebApi.Build failedBuild = CreateBuild($"https://failedbuild-{i}");
                failedBuild.Tags.Add(AzdoTags.AutoBuilder);
                failedBuild.Status = WebApi.BuildStatus.Completed;
                failedBuild.Result = WebApi.BuildResult.Failed;
                allBuilds.Add(failedBuild);
            }

            using TestContext context = new(
                allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos, new PagedList<WebApi.Build>(), allBuilds);
            await context.ExecuteCommandAsync();

            Dictionary<Subscription, IList<string>> expectedPathsBySubscription = new()
            {
                {
                    subscriptionInfos[0].Subscription,
                    new List<string>
                    {
                        dockerfile1Path
                    }
                }
            };

            context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: true, expectedPathsBySubscription);
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued builds for two
        /// subscriptions that have image paths.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_MultiSubscription()
        {
            const string repo1 = "repo1";
            const string repo2 = "repo2";

            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";
            const string dockerfile3Path = "dockerfile3/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }))))),
                new SubscriptionInfo(
                    CreateSubscription(repo2),
                    CreateManifest(
                        CreateRepo(
                            repo2,
                            CreateImage(
                                CreatePlatform(dockerfile2Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile3Path, new string[] { "tag2" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                },
                {
                    subscriptionInfos[1].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile3Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new List<List<SubscriptionRebuildInfo>>
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    },
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[1].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile2Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                },
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[1].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile3Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            using (TestContext context = new TestContext(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<string>> expectedPathsBySubscription =
                    new Dictionary<Subscription, IList<string>>
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<string>
                        {
                            dockerfile1Path
                        }
                    },
                    {
                        subscriptionInfos[1].Subscription,
                        new List<string>
                        {
                            dockerfile2Path,
                            dockerfile3Path
                        }
                    }
                };

                context.Verify(notificationPostCallCount: 2, isQueuedBuildExpected: true, expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies that the paths to be queued will be expanded for the entire graph of dependencies.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_GraphExpansion()
        {
            const string runtimeDepsRepo = "runtimedeps-repo";
            const string runtimeRepo = "runtime-repo";
            const string sdkRepo = "sdk-repo";
            const string aspnetRepo = "aspnet-repo";
            const string otherRepo = "other-repo";
            const string runtimeDepsDockerfilePath = "runtime-deps/Dockerfile";
            const string runtimeDockerfilePath = "runtime/Dockerfile";
            const string sdkDockerfilePath = "sdk/Dockerfile";
            const string aspnetDockerfilePath = "aspnet/Dockerfile";
            const string otherDockerfilePath = "other/Dockerfile";
            const string baseImage = "base1";
            const string baseImageDigest = "sha256:base1digest";
            const string otherImage = "other";
            const string otherImageDigest = "sha256:otherDigest";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription("repo1"),
                    CreateManifest(
                        CreateRepo(
                            runtimeDepsRepo,
                            CreateImage(
                                CreatePlatform(runtimeDepsDockerfilePath, new string[] { "tag1" }))),
                        CreateRepo(
                            runtimeRepo,
                            CreateImage(
                                CreatePlatformWithRepoBuildArg(runtimeDockerfilePath, runtimeDepsRepo, new string[] { "tag1" }))),
                        CreateRepo(
                            sdkRepo,
                            CreateImage(
                                CreatePlatformWithRepoBuildArg(sdkDockerfilePath, runtimeRepo, new string[] { "tag1" }))),
                        CreateRepo(
                            aspnetRepo,
                            CreateImage(
                                CreatePlatformWithRepoBuildArg(aspnetDockerfilePath, runtimeRepo, new string[] { "tag1" }))),
                        CreateRepo(
                            otherRepo,
                            CreateImage(
                                CreatePlatform(otherDockerfilePath, new string[] { "tag1" }))))
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(runtimeDepsDockerfilePath, new FromImageInfo(baseImage, baseImageDigest)),
                        new DockerfileInfo(runtimeDockerfilePath, new FromImageInfo($"{runtimeDepsRepo}:tag1", null)),
                        new DockerfileInfo(sdkDockerfilePath, new FromImageInfo($"{aspnetRepo}:tag1", null)),
                        new DockerfileInfo(aspnetDockerfilePath, new FromImageInfo($"{runtimeRepo}:tag1", null)),
                        new DockerfileInfo(otherDockerfilePath, new FromImageInfo(otherImage, otherImageDigest))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = runtimeDepsDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            TestContext context = new(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos);
            await context.ExecuteCommandAsync();

            Dictionary<Subscription, IList<string>> expectedPathsBySubscription = new()
            {
                {
                    subscriptionInfos[0].Subscription,
                    new List<string>
                    {
                        runtimeDepsDockerfilePath,
                        runtimeDockerfilePath,
                        aspnetDockerfilePath,
                        sdkDockerfilePath,
                    }
                }
            };

            context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: true, expectedPathsBySubscription);
        }

        /// <summary>
        /// Verifies that no build will be queued if no paths are specified.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_NoBaseImageChange()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id
                    }
                },
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id
                    }
                }
            };

            using (TestContext context = new TestContext(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                context.Verify(notificationPostCallCount: 0, isQueuedBuildExpected: false);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build a subscription is spread
        /// across multiple path sets.
        /// </summary>
        [Fact]
        public async Task QueueBuildCommand_Subscription_MultiSet()
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";
            const string dockerfile3Path = "dockerfile3/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" }),
                                CreatePlatform(dockerfile3Path, new string[] { "tag3" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile3Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new List<List<SubscriptionRebuildInfo>>
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile2Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                },
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile3Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.BaseImageChange
                                    }
                                }
                            }
                        }
                    }
                }
            };

            using (TestContext context = new(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<string>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<string>
                        {
                            dockerfile1Path,
                            dockerfile2Path,
                            dockerfile3Path
                        }
                    }
                };

                context.Verify(notificationPostCallCount: 1, isQueuedBuildExpected: true, expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the expected behavior due to an image's rebuild reason.
        /// </summary>
        [Theory]
        [InlineData(ImageRebuildReasonType.BaseImageChange, null, false, true)]
        [InlineData(ImageRebuildReasonType.BaseImageChange, null, true, true)]
        [InlineData(ImageRebuildReasonType.UpgradablePackage, null, false, false)]
        [InlineData(ImageRebuildReasonType.UpgradablePackage, null, true, true)]
        [InlineData(ImageRebuildReasonType.UpgradablePackage, ImageRebuildReasonType.BaseImageChange, false, true)]
        [InlineData(ImageRebuildReasonType.UpgradablePackage, ImageRebuildReasonType.BaseImageChange, true, true)]
        public async Task QueueBuildCommand_RebuildReasons(
            ImageRebuildReasonType reason1, ImageRebuildReasonType? reason2, bool enableUpgradablePackages, bool isQueuedBuildExpected)
        {
            const string repo = "repo1";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo),
                    CreateManifest(
                        CreateRepo(
                            repo,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))))
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest"))
                    }
                }
            };

            List<ImageRebuildReason> reasons = new()
            {
                new ImageRebuildReason
                {
                    ReasonType = reason1
                }
            };

            if (reason2 is not null)
            {
                reasons.Add(
                    new ImageRebuildReason
                    {
                        ReasonType = reason2.Value
                    });
            }

            List<List<SubscriptionRebuildInfo>> allSubscriptionImagePaths = new()
            {
                new List<SubscriptionRebuildInfo>
                {
                    new SubscriptionRebuildInfo
                    {
                        SubscriptionId = subscriptionInfos[0].Subscription.Id,
                        ImageRebuildInfos = new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = reasons
                            }
                        }
                    }
                }
            };

            TestContext context = new(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos);
            context.Command.Options.EnableUpgradablePackages = enableUpgradablePackages;

            await context.ExecuteCommandAsync();

            Dictionary<Subscription, IList<string>> expectedPathsBySubscription = new();
            if (isQueuedBuildExpected)
            {
                expectedPathsBySubscription[subscriptionInfos[0].Subscription] =
                    new List<string>
                    {
                        dockerfile1Path
                    };
            }

            context.Verify(
                notificationPostCallCount: 1,
                isQueuedBuildExpected: isQueuedBuildExpected,
                expectedPathsBySubscription);
        }

        /// <summary>
        /// Use this method to generate a unique repo owner name for the tests. This ensures that each test
        /// uses a different name and prevents collisions when running the tests in parallel. This is because
        /// the <see cref="QueueBuildCommand"/> generates temp folders partially based on the name of
        /// the repo owner.
        /// </summary>
        private static string GetRepoOwner([CallerMemberName] string testMethodName = null, string suffix = null)
        {
            return testMethodName + suffix;
        }

        private static Subscription CreateSubscription(
            string repoName,
            int index = 0,
            [CallerMemberName] string testMethodName = null)
        {
            return new Subscription
            {
                PipelineTrigger = new PipelineTrigger
                {
                    Id = 1,
                    PathVariable = "--my-path"
                },
                Manifest = new GitFile
                {
                    Branch = "testBranch" + index,
                    Repo = repoName,
                    Owner = GetRepoOwner(testMethodName, index.ToString()),
                    Path = "testmanifest.json"
                },
                ImageInfo = new GitFile
                {
                    Owner = "dotnetOwner",
                    Repo = "versionsRepo",
                    Branch = "mainBranch",
                    Path = "docker/image-info.json"
                }
            };
        }

        private static WebApi.Build CreateBuild(string url)
        {
            WebApi.Build build = new() { Uri = new Uri(url) };
            build.Links.AddLink("web", $"{url}/web");
            return build;
        }

        /// <summary>
        /// Sets up the test state from the provided metadata, executes the test, and verifies the results.
        /// </summary>
        private class TestContext : IDisposable
        {
            private const string BuildOrganization = "testOrg";
            private const string GitOwner = "git-owner";
            private const string GitRepo = "git-repo";
            private const string GitAccessToken = "git-pat";

            private readonly PagedList<WebApi.Build> inProgressBuilds;
            private readonly PagedList<WebApi.Build> allBuilds;
            private readonly List<string> filesToCleanup = new();
            private readonly List<string> foldersToCleanup = new();
            private readonly string subscriptionsPath;
            private readonly Mock<IBuildHttpClient> buildHttpClientMock;
            private readonly IEnumerable<IEnumerable<SubscriptionRebuildInfo>> allSubscriptionImagePaths;
            private readonly SubscriptionInfo[] _subscriptionInfos;
            private readonly Dictionary<GitFile, List<DockerfileInfo>> _dockerfileInfos;
            private readonly Mock<INotificationService> _notificationServiceMock;

            public QueueBuildCommand Command { get; }

            /// <summary>
            /// Initializes a new instance of <see cref="TestContext"/>.
            /// </summary>
            /// <param name="allSubscriptionImagePaths">Multiple sets of mappings between subscriptions and their associated image paths.</param>
            public TestContext(
                IEnumerable<IEnumerable<SubscriptionRebuildInfo>> allSubscriptionImagePaths,
                SubscriptionInfo[] subscriptionInfos,
                Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos)
                : this(allSubscriptionImagePaths, subscriptionInfos, dockerfileInfos,
                      new PagedList<WebApi.Build>(), new PagedList<WebApi.Build>())
            {
            }

            /// <summary>
            /// Initializes a new instance of <see cref="TestContext"/>.
            /// </summary>
            /// <param name="allSubscriptionImagePaths">Multiple sets of mappings between subscriptions and their associated image paths.</param>
            /// <param name="inProgressBuilds">The set of in-progress builds that should be configured.</param>
            /// <param name="allBuilds">The set of failed builds that should be configured.</param>
            public TestContext(
                IEnumerable<IEnumerable<SubscriptionRebuildInfo>> allSubscriptionImagePaths,
                SubscriptionInfo[] subscriptionInfos,
                Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos,
                PagedList<WebApi.Build> inProgressBuilds,
                PagedList<WebApi.Build> allBuilds)
            {
                this.allSubscriptionImagePaths = allSubscriptionImagePaths;
                _subscriptionInfos = subscriptionInfos;
                _dockerfileInfos = dockerfileInfos;
                this.inProgressBuilds = inProgressBuilds;
                this.allBuilds = allBuilds;

                this.subscriptionsPath = this.SerializeJsonObjectToTempFile(_subscriptionInfos.Select(info => info.Subscription).ToList());

                TeamProject project = new TeamProject
                {
                    Id = Guid.NewGuid()
                };

                Mock<IProjectHttpClient> projectHttpClientMock = CreateProjectHttpClientMock(project);
                this.buildHttpClientMock = CreateBuildHttpClientMock(project, this.inProgressBuilds, this.allBuilds);
                Mock<IVssConnectionFactory> connectionFactoryMock = CreateVssConnectionFactoryMock(
                    projectHttpClientMock, this.buildHttpClientMock);

                _notificationServiceMock = new Mock<INotificationService>();

                this.Command = this.CreateCommand(connectionFactoryMock);
            }

            public Task ExecuteCommandAsync()
            {
                return this.Command.ExecuteAsync();
            }

            /// <summary>
            /// Verifies the test execution to ensure the results match the expected state.
            /// </summary>
            /// <param name="notificationPostCallCount">
            /// Number of times a post to notify the GitHub repo is expected.
            /// </param>
            /// <param name="isQueuedBuildExpected">
            /// Indicates whether a build was expected to be queued.
            /// </param>
            /// <param name="expectedPathsBySubscription">
            /// A mapping of subscription metadata to the list of expected path args passed to the queued build, if any.
            /// </param>
            public void Verify(int notificationPostCallCount, bool isQueuedBuildExpected, IDictionary<Subscription, IList<string>> expectedPathsBySubscription = null)
            {
                _notificationServiceMock
                    .Verify(o => o.PostAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), $"https://github.com/{GitOwner}/{GitRepo}", GitAccessToken),
                        Times.Exactly(notificationPostCallCount));

                if (!isQueuedBuildExpected)
                {
                    this.buildHttpClientMock.Verify(o => o.QueueBuildAsync(It.IsAny<WebApi.Build>()), Times.Never);
                }
                else
                {
                    if (expectedPathsBySubscription == null)
                    {
                        throw new ArgumentNullException(nameof(expectedPathsBySubscription));
                    }

                    foreach (KeyValuePair<Subscription, IList<string>> kvp in expectedPathsBySubscription)
                    {
                        if (kvp.Value.Any())
                        {
                            this.buildHttpClientMock
                                .Verify(o =>
                                    o.QueueBuildAsync(
                                        It.Is<WebApi.Build>(build => FilterBuildToSubscription(build, kvp.Key, kvp.Value))));
                        }
                    }
                }
            }

            private string SerializeJsonObjectToTempFile(object jsonObject)
            {
                string path = Path.GetTempFileName();
                File.WriteAllText(path, JsonConvert.SerializeObject(jsonObject));
                this.filesToCleanup.Add(path);
                return path;
            }

            private QueueBuildCommand CreateCommand(Mock<IVssConnectionFactory> connectionFactoryMock)
            {
                Mock<ILoggerService> loggerServiceMock = new();
                
                IHttpClientProvider httpClientProvider = Helpers.Subscriptions.SubscriptionHelper.CreateHttpClientFactory(
                    _subscriptionInfos, _dockerfileInfos, () =>
                    {
                        string tempDir = Directory.CreateDirectory(
                            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
                        this.foldersToCleanup.Add(tempDir);
                        return tempDir;
                    });

                QueueBuildCommand command = new(connectionFactoryMock.Object, loggerServiceMock.Object, _notificationServiceMock.Object, httpClientProvider);
                command.Options.AzdoOptions.Organization = BuildOrganization;
                command.Options.AzdoOptions.AccessToken = "testToken";
                command.Options.SubscriptionsPath = this.subscriptionsPath;
                command.Options.AllSubscriptionImagePaths = this.allSubscriptionImagePaths
                    .Select(subscriptionImagePaths => JsonConvert.SerializeObject(subscriptionImagePaths.ToArray()));

                command.Options.GitOptions.Owner = GitOwner;
                command.Options.GitOptions.Repo = GitRepo;
                command.Options.GitOptions.AuthToken = GitAccessToken;

                return command;
            }

            private static Mock<IVssConnectionFactory> CreateVssConnectionFactoryMock(
                Mock<IProjectHttpClient> projectHttpClientMock,
                Mock<IBuildHttpClient> buildHttpClientMock)
            {
                Mock<IVssConnection> connectionMock = CreateVssConnectionMock(projectHttpClientMock, buildHttpClientMock);

                Mock<IVssConnectionFactory> connectionFactoryMock = new Mock<IVssConnectionFactory>();
                connectionFactoryMock
                    .Setup(o => o.Create(
                        It.Is<Uri>(uri => uri.ToString() == $"https://dev.azure.com/{BuildOrganization}"),
                        It.IsAny<VssCredentials>()))
                    .Returns(connectionMock.Object);
                return connectionFactoryMock;
            }

            private static Mock<IVssConnection> CreateVssConnectionMock(Mock<IProjectHttpClient> projectHttpClientMock,
                Mock<IBuildHttpClient> buildHttpClientMock)
            {
                Mock<IVssConnection> connectionMock = new Mock<IVssConnection>();
                connectionMock
                    .Setup(o => o.GetProjectHttpClient())
                    .Returns(projectHttpClientMock.Object);
                connectionMock
                    .Setup(o => o.GetBuildHttpClient())
                    .Returns(buildHttpClientMock.Object);
                return connectionMock;
            }

            private static Mock<IProjectHttpClient> CreateProjectHttpClientMock(TeamProject project)
            {
                Mock<IProjectHttpClient> projectHttpClientMock = new Mock<IProjectHttpClient>();
                projectHttpClientMock
                    .Setup(o => o.GetProjectAsync(It.IsAny<string>()))
                    .ReturnsAsync(project);
                return projectHttpClientMock;
            }

            private static Mock<IBuildHttpClient> CreateBuildHttpClientMock(TeamProject project,
                PagedList<WebApi.Build> inProgressBuilds, PagedList<WebApi.Build> failedBuilds)
            {
                WebApi.Build build = CreateBuild("https://contoso");

                Mock<IBuildHttpClient> buildHttpClientMock = new();
                buildHttpClientMock
                    .Setup(o => o.GetBuildsAsync(project.Id, It.IsAny<IEnumerable<int>>(), WebApi.BuildStatus.InProgress))
                    .ReturnsAsync(inProgressBuilds);

                buildHttpClientMock
                    .Setup(o => o.GetBuildsAsync(project.Id, It.IsAny<IEnumerable<int>>(), null))
                    .ReturnsAsync(failedBuilds);

                buildHttpClientMock
                    .Setup(o => o.QueueBuildAsync(It.IsAny<WebApi.Build>()))
                    .ReturnsAsync(build);

                return buildHttpClientMock;
            }

            /// <summary>
            /// Returns a value indicating whether the <see cref="Build"/> object contains the expected state.
            /// </summary>
            /// <param name="build">The <see cref="Build"/> to validate.</param>
            /// <param name="subscription">Subscription object that contains metadata to compare against the <paramref name="build"/>.</param>
            /// <param name="expectedPaths">The set of expected path arguments that should have been passed to the build.</param>
            private static bool FilterBuildToSubscription(WebApi.Build build, Subscription subscription, IList<string> expectedPaths)
            {
                return build.Definition.Id == subscription.PipelineTrigger.Id &&
                    build.SourceBranch == subscription.Manifest.Branch &&
                    FilterBuildToParameters(build.Parameters, subscription.PipelineTrigger.PathVariable, expectedPaths);
            }

            /// <summary>
            /// Returns a value indicating whether <paramref name="buildParametersJson"/> matches the expected results.
            /// </summary>
            /// <param name="buildParametersJson">The raw JSON parameters value that was provided to a <see cref="Build"/>.</param>
            /// <param name="pathVariable">Name of the path variable that the arguments are assigned to.</param>
            /// <param name="expectedPaths">The set of expected path arguments that should have been passed to the build.</param>
            private static bool FilterBuildToParameters(string buildParametersJson, string pathVariable, IList<string> expectedPaths)
            {
                JObject buildParameters = JsonConvert.DeserializeObject<JObject>(buildParametersJson);
                string pathString = buildParameters[pathVariable].ToString();
                IList<string> paths = pathString
                    .Split(" ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('\''))
                    .Except(new string[] { CliHelper.FormatAlias(ManifestFilterOptionsBuilder.PathOptionName) })
                    .ToList();
                return CompareLists(expectedPaths, paths);
            }

            /// <summary>
            /// Returns a value indicating whether the two lists are equivalent (order does not matter).
            /// </summary>
            private static bool CompareLists(IList<string> expectedPaths, IList<string> paths)
            {
                if (paths.Count != expectedPaths.Count)
                {
                    return false;
                }

                paths = paths
                    .OrderBy(p => p)
                    .ToList();
                expectedPaths = expectedPaths
                    .OrderBy(p => p)
                    .ToList();

                for (int i = 0; i < paths.Count; i++)
                {
                    if (paths[i] != expectedPaths[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            public void Dispose()
            {
                foreach (string file in this.filesToCleanup)
                {
                    File.Delete(file);
                }

                foreach (string folder in this.foldersToCleanup)
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        private class PagedList<T> : List<T>, IPagedList<T>
        {
            public string ContinuationToken => throw new NotImplementedException();
        }
    }
}
