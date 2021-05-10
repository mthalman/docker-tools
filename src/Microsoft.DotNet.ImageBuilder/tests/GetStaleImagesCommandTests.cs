// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.DotNet.ImageBuilder.Commands;
using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;
using Microsoft.DotNet.ImageBuilder.Models.Subscription;
using Microsoft.DotNet.ImageBuilder.Tests.Helpers;
using Microsoft.DotNet.ImageBuilder.Tests.Helpers.Subscriptions;
using Microsoft.DotNet.VersionTools.Automation;
using Microsoft.DotNet.VersionTools.Automation.GitHubApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using static Microsoft.DotNet.ImageBuilder.Tests.Helpers.ImageInfoHelper;
using static Microsoft.DotNet.ImageBuilder.Tests.Helpers.ManifestHelper;
using WebApi = Microsoft.TeamFoundation.Build.WebApi;

namespace Microsoft.DotNet.ImageBuilder.Tests
{
    public class GetStaleImagesCommandTests
    {
        private const string GitHubBranch = "my-branch";
        private const string GitHubRepo = "my-repo";
        private const string GitHubOwner = "my-owner";

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build for a basic
        /// scenario involving one image that has changed.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_SingleDigestChanged()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base1@sha256:base1digest-diff"),
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: "base2@sha256:base2digest")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest"))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // Only one of the images has a changed digest
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build for a multi-stage
        /// Dockerfile scenario involving one image that has changed.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_MultiStage_SingleDigestChanged()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base2@sha256:base2digest-diff"),
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: "base3@sha256:base3digest")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos =
                new Dictionary<GitFile, List<DockerfileInfo>>
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(
                            dockerfile1Path, 
                            new FromImageInfo("base1", "sha256:base1digest"),
                            new FromImageInfo("base2", "sha256:base2digest")),
                        new DockerfileInfo(
                            dockerfile2Path, 
                            new FromImageInfo("base2", "sha256:base2digest"), 
                            new FromImageInfo("base3", "sha256:base3digest"))
                    }
                }
            };

            using (TestContext context =
                new TestContext(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // The base image of the final stage has changed for only one of the images.
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies that a subscription will be skipped if it's associated with a different OS type than the command is assigned with.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_OsTypeFiltering_MatchingCommandFilter()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1";
            const string dockerfile2Path = "dockerfile2";
            const string dockerfile3Path = "dockerfile3";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1, osType: "windows"),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }, OS.Windows),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" }, OS.Linux),
                                CreatePlatform(dockerfile3Path, new string[] { "tag3" }, OS.Windows)))),
                    new ImageArtifactDetails()
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest")),
                        new DockerfileInfo(dockerfile3Path, new FromImageInfo("base3", "sha256:base3digest"))
                    }
                }
            };

            // Use windows here for the command's OsType filter which is the same as the subscription's OsType.
            // This should cause the subscription to be processed.
            const string commandOsType = "windows";

            using (TestContext context = new(subscriptionInfos, dockerfileInfos, commandOsType))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile3Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            }
                        }
                    }
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies that a subscription will be skipped if it's associated with a different OS type than the command is assigned with.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_OsTypeFiltering_NonMatchingCommandFilter()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1";
            const string dockerfile2Path = "dockerfile2";
            const string dockerfile3Path = "dockerfile3";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1, osType: "windows"),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }, OS.Windows),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" }, OS.Linux),
                                CreatePlatform(dockerfile3Path, new string[] { "tag3" }, OS.Windows)))),
                    new ImageArtifactDetails()
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest")),
                        new DockerfileInfo(dockerfile3Path, new FromImageInfo("base3", "sha256:base3digest"))
                    }
                }
            };

            // Use linux here for the command's OsType filter which is different than the subscription's OsType of Windows.
            // This should cause the subscription to be ignored.
            const string commandOsType = "linux";

            using (TestContext context = new(subscriptionInfos, dockerfileInfos, commandOsType))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when
        /// the images have no data reflected in the image info data.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_MissingImageInfo()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" })))),
                    new ImageArtifactDetails()
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest"))
                    }
                }
            };

            using (TestContext context =
                new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // Since neither of the images existed in the image info data, both should be queued.
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
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
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                        }
                    }
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued builds for two
        /// subscriptions that have changed images.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_MultiSubscription()
        {
            const string repo1 = "test-repo";
            const string repo2 = "test-repo2";
            const string repo3 = "test-repo3";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";
            const string dockerfile3Path = "dockerfile3/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1, 1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base1@sha256:base1digest-diff")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = repo2,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: "base2@sha256:base2digest-diff")
                                        }
                                    }
                                }
                            }
                        }
                    }
                ),
                new SubscriptionInfo(
                    CreateSubscription(repo2, 2),
                    CreateManifest(
                        CreateRepo(
                            repo2,
                            CreateImage(
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" }))),
                        CreateRepo(
                            repo3,
                            CreateImage(
                                CreatePlatform(dockerfile3Path, new string[] { "tag3" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base1@sha256:base1digest-diff")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = repo2,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: "base2@sha256:base2digest-diff")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = repo3,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile3Path,
                                                baseImageDigest: "base3@sha256:base3digest")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
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
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest")),
                        new DockerfileInfo(dockerfile3Path, new FromImageInfo("base3", "sha256:base3digest"))
                    }
                }
            };

            using (TestContext context =
                new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                    {
                        subscriptionInfos[1].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies that a base image's digest will be cached and not pulled for a subsequent image.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_BaseImageCaching()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string dockerfile2Path = "dockerfile2/Dockerfile";
            const string baseImage = "base1";
            const string baseImageDigest = "sha256:base1digest";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images = new List<ImageData>
                                {
                                    new ImageData
                                    {
                                        Platforms = new List<PlatformData>
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: baseImageDigest + "-diff"),
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: baseImageDigest + "-diff")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };


            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo(baseImage, baseImageDigest)),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo(baseImage, baseImageDigest))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // Both of the images has a changed digest
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);

                context.ManifestToolServiceMock
                    .Verify(o => o.Inspect(baseImage, false), Times.Once);
            }
        }

        /// <summary>
        /// Verifies that no build will be queued if the base image has not changed.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_NoBaseImageChange()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string baseImage = "base1";
            const string baseImageDigest = "base1digest";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: $"{baseImage}@{baseImageDigest}")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo(baseImage, baseImageDigest))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // No paths are expected
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new();

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when
        /// an image has an upgradable package.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_UpgradablePackage()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string baseImage = "base1";
            const string baseImageDigest = "base1digest";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: $"{baseImage}@{baseImageDigest}")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo(baseImage, baseImageDigest))
                    }
                }
            };

            using TestContext context = new(subscriptionInfos, dockerfileInfos);
            context.UpgradablePackages.Add("package1,1.0.1,1.0.2");

            await context.ExecuteCommandAsync();

            Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
            {
                {
                    subscriptionInfos[0].Subscription,
                    new List<ImageRebuildInfo>
                    {
                        new ImageRebuildInfo
                        {
                            DockerfilePath = dockerfile1Path,
                            Reasons = new List<ImageRebuildReason>
                            {
                                new ImageRebuildReason
                                {
                                    ReasonType = ImageRebuildReasonType.UpgradablePackage
                                }
                            }
                        }
                    }
                }
            };

            context.Verify(expectedPathsBySubscription);
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when
        /// an image has an upgradable package and a digest change.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_UpgradablePackageAndDigestChange()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";
            const string baseImage = "base1";
            const string baseImageDigest = "sha256:base1digest";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: $"{baseImage}@{baseImageDigest}-diff")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo(baseImage, baseImageDigest))
                    }
                }
            };

            using TestContext context = new(subscriptionInfos, dockerfileInfos);
            context.UpgradablePackages.Add("package1");

            await context.ExecuteCommandAsync();

            Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
            {
                {
                    subscriptionInfos[0].Subscription,
                    new List<ImageRebuildInfo>
                    {
                        new ImageRebuildInfo
                        {
                            DockerfilePath = dockerfile1Path,
                            Reasons = new List<ImageRebuildReason>
                            {
                                // Even though the image has an upgradable package, that check should be bypassed since
                                // a base image change is detected first.
                                new ImageRebuildReason
                                {
                                    ReasonType = ImageRebuildReasonType.BaseImageChange
                                }
                            }
                        }
                    }
                }
            };

            context.Verify(expectedPathsBySubscription);
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when
        /// a base image changes where the image referencing that base image has other
        /// images dependent upon it.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_DependencyGraph()
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
            const string runtimeDepsDigest = "sha256:abc";
            const string runtimeDigest = "sha256:def";
            const string aspnetDigest = "sha256:123";

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
                                CreatePlatform(otherDockerfilePath, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = runtimeDepsRepo,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                runtimeDepsDockerfilePath,
                                                baseImageDigest: $"{baseImage}@{baseImageDigest}-diff")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = runtimeRepo,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                runtimeDockerfilePath,
                                                baseImageDigest: $"{runtimeDepsRepo}@{runtimeDepsDigest}")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = sdkRepo,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                sdkDockerfilePath,
                                                baseImageDigest: $"{aspnetRepo}@{aspnetDigest}")
                                        }
                                    }
                                }
                            },
                            new RepoData
                            {
                                Repo = aspnetRepo,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                aspnetDockerfilePath,
                                                baseImageDigest: $"{runtimeRepo}@{runtimeDigest}")
                                        }
                                    }
                                }
                            },
                            // Include an image that has not been changed and should not be included in the expected paths.
                            new RepoData
                            {
                                Repo = otherRepo,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                otherDockerfilePath,
                                                baseImageDigest: $"{otherImage}@{otherImageDigest}")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(runtimeDepsDockerfilePath, new FromImageInfo(baseImage, baseImageDigest)),
                        new DockerfileInfo(runtimeDockerfilePath, new FromImageInfo($"{runtimeDepsRepo}:tag1", runtimeDepsDigest)),
                        new DockerfileInfo(sdkDockerfilePath, new FromImageInfo($"{aspnetRepo}:tag1", aspnetDigest)),
                        new DockerfileInfo(aspnetDockerfilePath, new FromImageInfo($"{runtimeRepo}:tag1", runtimeDigest)),
                        new DockerfileInfo(otherDockerfilePath, new FromImageInfo(otherImage, otherImageDigest))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when
        /// a base image changes where the image referencing that base image has other
        /// images dependent upon it and no image info data exists.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_DependencyGraph_MissingImageInfo()
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
            const string baseImageDigest = "base1digest";
            const string otherImage = "other";
            const string otherImageDigest = "otherDigest";

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
                                CreatePlatformWithRepoBuildArg(sdkDockerfilePath, aspnetRepo, new string[] { "tag1" }))),
                        CreateRepo(
                            aspnetRepo,
                            CreateImage(
                                CreatePlatformWithRepoBuildArg(aspnetDockerfilePath, runtimeRepo, new string[] { "tag1" }))),
                        CreateRepo(
                            otherRepo,
                            CreateImage(
                                CreatePlatform(otherDockerfilePath, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                    }
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

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = runtimeDepsDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = runtimeDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = aspnetDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = sdkDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            },
                            new ImageRebuildInfo
                            {
                                DockerfilePath = otherDockerfilePath,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            }
                        }
                    }
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build when an image
        /// built from a custom named Dockerfile.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_CustomDockerfilePath()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "path/to/Dockerfile.custom";
            const string dockerfile2Path = "path/to/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }),
                                CreatePlatform(dockerfile2Path, new string[] { "tag2" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base1@sha256:base1digest-diff"),
                                            CreatePlatform(
                                                dockerfile2Path,
                                                baseImageDigest: "base2@sha256:base2digest")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "sha256:base1digest")),
                        new DockerfileInfo(dockerfile2Path, new FromImageInfo("base2", "sha256:base2digest"))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // Only one of the images has a changed digest
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies an image will be marked to be rebuilt if its base image is not included in the list of image data.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_NoExistingImageData()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" })))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "sha256:base1digest")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base2", "sha256:base2digest")),
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
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
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Verifies the correct path arguments are passed to the queued build for a
        /// scenario involving two platforms sharing the same Dockerfile.
        /// </summary>
        [Fact]
        public async Task GetStaleImagesCommand_SharedDockerfile()
        {
            const string repo1 = "test-repo";
            const string dockerfile1Path = "dockerfile1/Dockerfile";

            SubscriptionInfo[] subscriptionInfos = new SubscriptionInfo[]
            {
                new SubscriptionInfo(
                    CreateSubscription(repo1),
                    CreateManifest(
                        CreateRepo(
                            repo1,
                            CreateImage(
                                CreatePlatform(dockerfile1Path, new string[] { "tag1" }, osVersion: "alpine3.10"),
                                CreatePlatform(dockerfile1Path, new string[] { "tag2" }, osVersion: "alpine3.11")))),
                    new ImageArtifactDetails
                    {
                        Repos =
                        {
                            new RepoData
                            {
                                Repo = repo1,
                                Images =
                                {
                                    new ImageData
                                    {
                                        Platforms =
                                        {
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base1digest",
                                                osVersion: "Alpine 3.10"),
                                            CreatePlatform(
                                                dockerfile1Path,
                                                baseImageDigest: "base2digest-diff",
                                                osVersion: "Alpine 3.11")
                                        }
                                    }
                                }
                            }
                        }
                    }
                )
            };

            Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos = new()
            {
                {
                    subscriptionInfos[0].Subscription.Manifest,
                    new List<DockerfileInfo>
                    {
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base1", "base1digest")),
                        new DockerfileInfo(dockerfile1Path, new FromImageInfo("base2", "base2digest"))
                    }
                }
            };

            using (TestContext context = new(subscriptionInfos, dockerfileInfos))
            {
                await context.ExecuteCommandAsync();

                // Only one of the images has a changed digest
                Dictionary<Subscription, IList<ImageRebuildInfo>> expectedPathsBySubscription = new()
                {
                    {
                        subscriptionInfos[0].Subscription,
                        new List<ImageRebuildInfo>
                        {
                            new ImageRebuildInfo
                            {
                                DockerfilePath = dockerfile1Path,
                                Reasons = new List<ImageRebuildReason>
                                {
                                    new ImageRebuildReason
                                    {
                                        ReasonType = ImageRebuildReasonType.MissingImageInfo
                                    }
                                }
                            }
                        }
                    }
                };

                context.Verify(expectedPathsBySubscription);
            }
        }

        /// <summary>
        /// Use this method to generate a unique repo owner name for the tests. This ensures that each test
        /// uses a different name and prevents collisions when running the tests in parallel. This is because
        /// the <see cref="GetStaleImagesCommand"/> generates temp folders partially based on the name of
        /// the repo owner.
        /// </summary>
        private static string GetRepoOwner([CallerMemberName] string testMethodName = null, string suffix = null)
        {
            return testMethodName + suffix;
        }

        private static Subscription CreateSubscription(
            string repoName,
            int index = 0,
            string osType = null,
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

                    Owner = GetStaleImagesCommandTests.GitHubOwner,
                    Repo = GetStaleImagesCommandTests.GitHubRepo,
                    Branch = GetStaleImagesCommandTests.GitHubBranch,
                    Path = "docker/image-info.json"
                },
                OsType = osType
            };
        }

        /// <summary>
        /// Sets up the test state from the provided metadata, executes the test, and verifies the results.
        /// </summary>
        private class TestContext : IDisposable
        {
            private const string VariableName = "my-var";

            private readonly List<string> filesToCleanup = new();
            private readonly List<string> foldersToCleanup = new();
            private readonly Dictionary<string, string> imageDigests = new();
            private readonly string subscriptionsPath;
            private readonly IHttpClientProvider httpClientFactory;
            private readonly Mock<ILoggerService> loggerServiceMock = new();
            private readonly string osType;
            private readonly IGitHubClientFactory gitHubClientFactory;

            private GetStaleImagesCommand command;

            public Mock<IManifestToolService> ManifestToolServiceMock { get; }

            public List<string> InstalledPackages { get; } = new();
            public List<string> UpgradablePackages { get; } = new();

            /// <summary>
            /// Initializes a new instance of <see cref="TestContext"/>.
            /// </summary>
            /// <param name="subscriptionInfos">Mapping of data to subscriptions.</param>
            /// <param name="dockerfileInfos">A mapping of Git repos to their associated set of Dockerfiles.</param>
            /// <param name="osType">The OS type to filter the command with.</param>
            public TestContext(
                SubscriptionInfo[] subscriptionInfos,
                Dictionary<GitFile, List<DockerfileInfo>> dockerfileInfos,
                string osType = "*")
            {
                this.osType = osType;
                this.subscriptionsPath = this.SerializeJsonObjectToTempFile(
                    subscriptionInfos.Select(tuple => tuple.Subscription).ToArray());

                // Cache image digests lookup
                foreach (FromImageInfo fromImage in 
                    dockerfileInfos.Values.SelectMany(infos => infos).SelectMany(info => info.FromImages))
                {
                    if (fromImage.Name != null)
                    {
                        this.imageDigests[fromImage.Name] = fromImage.Digest;
                    }
                }

                TeamProject project = new TeamProject
                {
                    Id = Guid.NewGuid()
                };

                this.httpClientFactory = Helpers.Subscriptions.SubscriptionHelper.CreateHttpClientFactory(
                    subscriptionInfos, dockerfileInfos, () =>
                    {
                        string tempDir = Directory.CreateDirectory(
                            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
                        this.foldersToCleanup.Add(tempDir);
                        return tempDir;
                    });
                this.gitHubClientFactory = CreateGitHubClientFactory(subscriptionInfos);

                this.ManifestToolServiceMock = this.CreateManifestToolServiceMock();
            }

            public Task ExecuteCommandAsync()
            {
                this.command = this.CreateCommand();
                return this.command.ExecuteAsync();
            }

            /// <summary>
            /// Verifies the test execution to ensure the results match the expected state.
            /// </summary>
            public void Verify(IDictionary<Subscription, IList<ImageRebuildInfo>> expectedImageRebuildInfosBySubscription)
            {
                IInvocation invocation = this.loggerServiceMock.Invocations
                    .First(invocation => invocation.Method.Name == nameof(ILoggerService.WriteMessage) &&
                        invocation.Arguments[0].ToString().StartsWith("##vso"));
                
                string message = invocation.Arguments[0].ToString();
                int variableNameStartIndex = message.IndexOf("=") + 1;
                string actualVariableName = message.Substring(variableNameStartIndex, message.IndexOf(";") - variableNameStartIndex);
                Assert.Equal(VariableName, actualVariableName);

                string variableValue = message
                    .Substring(message.IndexOf("]") + 1);

                SubscriptionRebuildInfo[] pathsBySubscription = 
                    JsonConvert.DeserializeObject<SubscriptionRebuildInfo[]>(variableValue.Replace("\\\"", "\""));

                Assert.Equal(expectedImageRebuildInfosBySubscription.Count, pathsBySubscription.Length);

                foreach (KeyValuePair<Subscription, IList<ImageRebuildInfo>> kvp in expectedImageRebuildInfosBySubscription)
                {
                    SubscriptionRebuildInfo actualRebuildInfo = pathsBySubscription
                        .First(imagePaths => imagePaths.SubscriptionId == kvp.Key.Id);

                    Assert.Equal(kvp.Value.Count, actualRebuildInfo.ImageRebuildInfos.Count);

                    for (int i = 0; i < actualRebuildInfo.ImageRebuildInfos.Count; i++)
                    {
                        ImageRebuildInfo actualImageRebuildInfo = actualRebuildInfo.ImageRebuildInfos[i];
                        ImageRebuildInfo expectedImageRebuildInfo = kvp.Value[i];

                        Assert.Equal(expectedImageRebuildInfo.DockerfilePath, actualImageRebuildInfo.DockerfilePath);
                        Assert.Equal(expectedImageRebuildInfo.Reasons.Count, actualImageRebuildInfo.Reasons.Count);

                        for (int j = 0; j < actualImageRebuildInfo.Reasons.Count; j++)
                        {
                            ImageRebuildReason actualReason = actualImageRebuildInfo.Reasons[j];
                            ImageRebuildReason expectedReason = expectedImageRebuildInfo.Reasons[j];
                            Assert.Equal(expectedReason.ReasonType, actualReason.ReasonType);
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

            private GetStaleImagesCommand CreateCommand()
            {
                const string GetInstalledPackagesScriptPath = "tools//get-installed.sh";
                const string GetUpgradablePackagesScriptPath = "tools//get-upgradable.sh";

                Mock<IDockerService> dockerServiceMock = new();
                dockerServiceMock
                    .Setup(obj => obj.Run(It.IsAny<string>(), It.Is<string>(cmd => cmd.Contains(Path.GetFileName(GetInstalledPackagesScriptPath))), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<bool>()))
                    .Returns(string.Join(Environment.NewLine, InstalledPackages));

                dockerServiceMock
                    .Setup(obj => obj.Copy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                    .Callback((string src, string dst, bool isDryRun) =>
                    {
                        File.WriteAllLines(dst, UpgradablePackages);
                    });

                GetStaleImagesCommand command = new GetStaleImagesCommand(
                    this.ManifestToolServiceMock.Object,
                    this.httpClientFactory,
                    this.loggerServiceMock.Object,
                    this.gitHubClientFactory,
                    dockerServiceMock.Object);
                command.Options.SubscriptionOptions.SubscriptionsPath = this.subscriptionsPath;
                command.Options.VariableName = VariableName;
                command.Options.FilterOptions.OsType = this.osType;
                command.Options.GitOptions.Email = "test";
                command.Options.GitOptions.Username = "test";
                command.Options.GitOptions.AuthToken = "test";
                command.Options.GetInstalledPackagesScriptPath = GetInstalledPackagesScriptPath;
                command.Options.GetUpgradablePackagesScriptPath = GetUpgradablePackagesScriptPath;
                return command;
            }

            private IGitHubClientFactory CreateGitHubClientFactory(SubscriptionInfo[] subscriptionInfos)
            {
                Mock<IGitHubClient> gitHubClientMock = new Mock<IGitHubClient>();

                foreach (SubscriptionInfo subscriptionInfo in subscriptionInfos)
                {
                    if (subscriptionInfo.ImageInfo != null)
                    {
                        string imageInfoContents = JsonConvert.SerializeObject(subscriptionInfo.ImageInfo);
                        gitHubClientMock
                            .Setup(o => o.GetGitHubFileContentsAsync(It.IsAny<string>(), It.Is<GitHubBranch>(branch => IsMatchingBranch(branch))))
                            .ReturnsAsync(imageInfoContents);
                    }
                }

                Mock<IGitHubClientFactory> gitHubClientFactoryMock = new Mock<IGitHubClientFactory>();
                gitHubClientFactoryMock
                    .Setup(o => o.GetClient(It.IsAny<GitHubAuth>(), false))
                    .Returns(gitHubClientMock.Object);

                return gitHubClientFactoryMock.Object;
            }

            private static bool IsMatchingBranch(GitHubBranch branch)
            {
                return branch.Name == GitHubBranch &&
                    branch.Project.Name == GitHubRepo &&
                    branch.Project.Owner == GitHubOwner;
            }

            private Mock<IManifestToolService> CreateManifestToolServiceMock()
            {
                Mock<IManifestToolService> manifestToolServiceMock = new Mock<IManifestToolService>();
                manifestToolServiceMock
                    .Setup(o => o.Inspect(It.IsAny<string>(), false))
                    .Returns((string image, bool isDryRun) =>
                        ManifestToolServiceHelper.CreateTagManifest(
                            ManifestToolService.ManifestListMediaType, this.imageDigests[image]));
                return manifestToolServiceMock;
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
                    .Split("--path ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('\''))
                    .ToList();
                return TestHelper.CompareLists(expectedPaths, paths);
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

                this.command?.Dispose();
            }
        }

        private class PagedList<T> : List<T>, IPagedList<T>
        {
            public string ContinuationToken => throw new NotImplementedException();
        }
    }
}
